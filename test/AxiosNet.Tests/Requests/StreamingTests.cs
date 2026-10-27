
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 流式响应生命周期：连接租约随流释放、拦截器替换响应、断流映射。
/// </summary>
public sealed class StreamingTests
{
    // ===============================================================
    // M1：流式响应被响应拦截器换成非 Stream 的 Data 时，连接租约必须归还
    // ===============================================================

    [Fact]
    public async Task M1_StreamResponse_InterceptorReplacingData_LeasesReturned()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"n\":1}\n"),
            ContentType: "application/x-ndjson"));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });
        using var client = new AxiosClient(pool: pool);

        client.Interceptors.Response.Use(response =>
        {
            response.Data = null;
            return response;
        });

        var response = await client.GetAsync<Stream>(server.Url + "stream");

        Assert.Equal(200, response.Status);
        Assert.Null(response.Data);

        // 拦截器丢掉了流式正文：包装流（持有租约 + HttpResponseMessage）必须在这里被释放
        Assert.Equal(0, ActiveLeases(pool));
    }

    [Fact]
    public async Task StreamResponse_WhenInterceptorThrows_ReleasesLease()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.Zero,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        client.Interceptors.Response.Use(_ =>
            Task.FromException<AxiosResponse>(new InvalidOperationException("interceptor boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync(server.Url + "stream", c => c.ResponseType = ResponseType.Stream));

        var stats = pool.GetStatistics();
        Assert.All(stats.Endpoints, e => Assert.Equal(0, e.ActiveLeases));
        Assert.Equal(1, pool.EvictIdleEndpoints());
    }

    [Fact]
    public async Task StreamResponse_WhenInterceptorReturnsNewResponse_ReleasesOriginalLease()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.Zero,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        client.Interceptors.Response.Use(_ => Task.FromResult(new AxiosResponse
        {
            Data = "replaced",
            Status = 200
        }));

        var response = await client.GetAsync(server.Url + "stream", c => c.ResponseType = ResponseType.Stream);

        Assert.Equal("replaced", response.Data);
        Assert.All(pool.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));
        Assert.Equal(1, pool.EvictIdleEndpoints());
    }

    // ------------------------------------------------------------------
    // 泛型参数与 responseType 不匹配时的流式正文
    // ------------------------------------------------------------------

    [Fact]
    public async Task TypedRequest_WithMismatchedStreamResponse_ReleasesLease()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // responseType: stream 的正文是 Stream(<Stream>)，GetAsync<byte[]> 的转换必然失败。
        // 此时响应交不到调用方手里，正文必须由库释放 —— 否则租约永不归还（之前 ActiveLeases 停在 1）。
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>(server.Url, c => c.ResponseType = ResponseType.Stream));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        var endpoints = client.Pool!.GetStatistics().Endpoints;
        Assert.All(endpoints, endpoint => Assert.Equal(0, endpoint.ActiveLeases));
    }

    [Fact]
    public async Task TypedRequest_AfterMismatchedStreamResponse_PoolStillUsable()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>(server.Url, c => c.ResponseType = ResponseType.Stream));

        var response = await client.GetAsync(server.Url);
        Assert.Equal(200, response.Status);
        Assert.All(
            client.Pool!.GetStatistics().Endpoints,
            endpoint => Assert.Equal(0, endpoint.ActiveLeases));
    }

    // 流式正文截断与非流式同错误模型

    [Fact]
    public async Task TruncatedStreamingBody_IsWrappedAsNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("0123456789"),
            ContentType: "application/octet-stream",
            ContentLengthOverride: 100,
            CloseConnection: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "truncated");
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.IsAssignableFrom<IOException>(ex.Cause);
    }

    /// <summary>验证 Timeout=0 时正文截断同样报 ERR_NETWORK。</summary>
    [Fact]
    public async Task TruncatedStreamingBody_WithoutTimeout_IsStillNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("0123456789"),
            ContentType: "application/octet-stream",
            ContentLengthOverride: 100,
            CloseConnection: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "truncated", c => c.Timeout = TimeSpan.Zero);
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    // ---------------------------------------------------------------
    // 客户端关闭不得打断进行中的流式下载
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_CompletesAfterClientDispose_AndReleasesEndpoint()
    {
        var payload = new byte[200 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        var client = new AxiosClient();
        var response = await client.GetAsync<Stream>(server.Url + "file");
        var stream = response.Data!;

        // 响应头已经拿到、正文还在调用方手里：此时关闭客户端不应打断这次下载。
        // 池会把仍有活跃租约的节点标记为 DisposeWhenIdle，而不是直接释放 Handler。
        client.Dispose();

        await using (stream)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination);

            Assert.Equal(payload.Length, destination.Length);
            Assert.Equal(payload, destination.ToArray());
        }

        // 租约归还时节点自行摘除并释放：池里不该残留这个 origin
        Assert.Equal(0, client.Pool!.GetStatistics().EndpointCount);

        // 重复 Dispose 幂等
        client.Dispose();
    }

    // ---------------------------------------------------------------
    // 租约归还：Dispose / DisposeAsync / 中途放弃
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_DisposeAsync_ReturnsLeaseAndKeepsConnectionReusable()
    {
        const int size = 128 * 1024;
        await using var server = TestHttpServer.Start(req => req.Path == "/next"
            ? TestJson.Response(new { ok = true })
            : new TestResponse(Body: new byte[size], ContentType: "application/octet-stream"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "file");
        await using (var stream = response.Data!)
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.All(client.Pool!.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));

        // 正文读尽后连接是干净的，下一次请求应当复用它
        var next = await client.GetAsync<JsonElement>(server.Url + "next");
        Assert.Equal(200, next.Status);
        Assert.Equal(1, server.TotalConnections);
    }

    [Fact]
    public async Task StreamResponse_PartialReadThenDispose_ReturnsLeaseAndPoolStaysUsable()
    {
        await using var server = TestHttpServer.Start(req => req.Path == "/next"
            ? TestJson.Response(new { ok = true })
            : new TestResponse(Body: new byte[256 * 1024], ContentType: "application/octet-stream"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "file");
        var stream = response.Data!;
        var buffer = new byte[1];

        Assert.Equal(1, await stream.ReadAsync(buffer, 0, 1));

        // 只读了一个字节就放弃：租约必须归还，否则这个 origin 再也不会被驱逐
        stream.Dispose();

        Assert.All(client.Pool!.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));

        // 池仍然可用（读了一半的连接会被传输层丢弃，但下一次请求必须成功）
        var next = await client.GetAsync<JsonElement>(server.Url + "next");
        Assert.Equal(200, next.Status);
    }

    // ---------------------------------------------------------------
    // P0-1：流式响应遇非 2xx 必须释放响应体，否则连接被永久占死
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamingErrorResponse_ReleasesConnectionInsteadOfHangingPool()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(StatusCode: 404, Body: Encoding.UTF8.GetBytes(new string('x', 8192)), ContentType: "text/plain"));

        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { MaxConnectionsPerServer = 1 });

        for (var i = 0; i < 3; i++)
        {
            var ex = await Assert.ThrowsAsync<AxiosException>(() => client
                .GetAsync(server.Url + "missing", c => c.ResponseType = ResponseType.Stream)
                .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
            Assert.Equal(404, ex.Status);

            // 失败响应没有读者，正文必须被丢弃，不能挂在异常上占着连接
            Assert.Null(ex.Response!.Data);
        }
    }

    [Fact]
    public async Task StreamingResponse_HoldsLeaseUntilStreamDisposed()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"ok\":true}")));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.Zero,   // 任何「空闲」节点都可以立即驱逐
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);
        client.Defaults.ResponseType = ResponseType.Stream;

        var response = await client.GetAsync(server.Url + "ping");
        var stream = Assert.IsAssignableFrom<Stream>(response.Data);

        // 下载进行中：租约未归还，节点不能被驱逐
        var during = pool.GetStatistics().Endpoints.Single();
        Assert.Equal(1, during.ActiveLeases);
        Assert.Equal(0, pool.EvictIdleEndpoints());

        await stream.CopyToAsync(Stream.Null);
        await stream.DisposeAsync();

        var after = pool.GetStatistics().Endpoints.Single();
        Assert.Equal(0, after.ActiveLeases);
        Assert.Equal(1, pool.EvictIdleEndpoints());
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------


    private static async Task DrainAsync<T>(IAsyncEnumerable<T>? source)
    {
        if (source is null)
        {
            return;
        }

        await foreach (var _ in source.ConfigureAwait(false))
        {
        }
    }

    private static int ActiveLeases(HttpConnectionPool pool)
        => pool.GetStatistics().Endpoints.Sum(e => e.ActiveLeases);


    /// <summary>每次都返回同一段 NDJSON 正文的处理器。</summary>
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FixedBodyHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(_body))
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return Task.FromResult(response);
        }
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }


    /// <summary>Dispose 时阻塞到手动放行的 Handler，用于验证「释放不在锁内执行」。</summary>
    private sealed class BlockingDisposeHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }


    /// <summary>记录 Handler 创建/释放次数：用于断言驱逐与关闭「不漏释放、不重复释放」。</summary>
    private sealed class HandlerLedger
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public HttpMessageHandler Create()
        {
            Interlocked.Increment(ref _created);
            return new LedgerHandler(this);
        }

        private sealed class LedgerHandler : HttpMessageHandler
        {
            private readonly HandlerLedger _owner;
            private int _disposed;

            public LedgerHandler(HandlerLedger owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                base.Dispose(disposing);
            }
        }
    }

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。</para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);


}