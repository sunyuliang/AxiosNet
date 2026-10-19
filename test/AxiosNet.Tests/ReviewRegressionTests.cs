using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 全项目体检后的回归用例：每个用例对应一处已复现的缺陷，修复前后必须能区分。
/// </summary>
public sealed class ReviewRegressionTests
{
    // ---------------------------------------------------------------
    // P0-1：流式响应遇非 2xx 必须释放响应体，否则连接被永久占死
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamingErrorResponse_ReleasesConnectionInsteadOfHangingPool()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 404,
            Body: Encoding.UTF8.GetBytes(new string('x', 8192)),
            ContentType: "text/plain"));

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

    // ---------------------------------------------------------------
    // P0-3：错误模型补洞（截断响应 / 非 JSON 的 dynamic）
    // ---------------------------------------------------------------

    [Fact]
    public async Task TruncatedResponseBody_IsWrappedAsNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"partial\":1"),
            ContentType: "application/json",
            CloseConnection: true,
            ContentLengthOverride: 4096));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "truncated"));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.NotNull(ex.Cause);
        Assert.IsAssignableFrom<IOException>(ex.Cause);
    }

    [Fact]
    public async Task DynamicResponse_NonJsonBody_IsWrappedAsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("<html>gateway error</html>"),
            ContentType: "text/html"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<dynamic>(server.Url + "html"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.Cause);
    }

    // ---------------------------------------------------------------
    // P0-2：运行期改默认请求头（Token 刷新）不能打死并发请求
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConcurrentDefaultsHeaderMutation_DoesNotBreakInFlightRequests()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                client.Defaults.Headers.Set("X-Token", "v" + i++);
                client.Defaults.Headers.Delete("X-Token");
            }
        });

        var failures = new List<Exception>();
        for (var i = 0; i < 300; i++)
        {
            try
            {
                await client.GetAsync(server.Url + "ping");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        stop.Cancel();
        await writer;

        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------
    // P1：配置合并与序列化细节
    // ---------------------------------------------------------------

    [Fact]
    public void ParamsObjectWithIndexer_IsSkippedInsteadOfThrowing()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" });

        var uri = client.GetUri(c =>
        {
            c.Url = "search";
            c.Params = new WithIndexer { Name = "n", Page = 2 };
        });

        Assert.Contains("Name=n", uri);
        Assert.Contains("Page=2", uri);
    }

    [Fact]
    public async Task PerRequestZeroTimeout_DisablesInstanceDefaultTimeout()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(400);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { Timeout = TimeSpan.FromMilliseconds(100) });

        // 显式 Timeout=0 必须能覆盖实例默认值（此前被 "> 0" 判断吞掉）
        var response = await client.GetAsync(server.Url + "slow", c => c.Timeout = TimeSpan.Zero);

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task PerRequestConfig_IsNotMutatedByShortcut()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var config = new AxiosConfig();
        config.Headers.Set("X-Trace", "abc");

        await client.GetAsync(server.Url + "ping", config);

        // 调用方的 config 不能被就地写入 Url/Method；否则同一个 config 并发复用会串请求
        Assert.Null(config.Url);
        Assert.Null(config.BaseAddress);
    }

    // ---------------------------------------------------------------
    // P1：流式响应的超时与租约
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamTimeout_AppliesToBodyRead()
    {
        await using var server = StartSlowBodyServer(TimeSpan.FromSeconds(3));

        using var client = new AxiosClient();
        await client.WarmUpAsync(server.Url + "warmup");

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = BodyReadTimeout);

        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));
        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    /// <summary>
    /// 同步 Read 也必须能在等待中被超时打断：令牌交给底层那一次 I/O，
    /// 而不是发起前预检一次（否则读卡在 socket 上时超时形同虚设）。
    /// </summary>
    [Fact]
    public async Task StreamTimeout_InterruptsBlockedSyncRead()
    {
        await using var server = StartSlowBodyServer(TimeSpan.FromSeconds(3));

        using var client = new AxiosClient();
        await client.WarmUpAsync(server.Url + "warmup");

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = BodyReadTimeout);

        await using var stream = response.Data!;

        // 放到后台线程阻塞，避免 xunit 的同步上下文被同步读占住
        var ex = await Task.Run(() => Assert.Throws<AxiosException>(
            () => stream.Read(new byte[16], 0, 16)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    /// <summary>
    /// 同步 Read 上调用方自己传的令牌语义不变：外部取消 → ERR_CANCELED（这里验证外部令牌中途取消）。
    /// </summary>
    [Fact]
    public async Task StreamExternalCancel_InterruptsBlockedSyncRead()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"ok\":true}"),
            BodyDelay: TimeSpan.FromMilliseconds(800)));

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = TimeSpan.FromSeconds(30),
            cts.Token);

        await using var stream = response.Data!;
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        var ex = await Task.Run(() => Assert.Throws<AxiosException>(
            () => stream.Read(new byte[16], 0, 16)));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
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

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算：500ms 是「连接已预热」下的宽裕值（本地往返只有微秒级），
    /// 又远小于正文延迟（3 秒），保证超时一定落在正文阶段而不是响应头阶段。
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 「先发响应头、再延迟正文」的测试服务器，并为预热留一个立即返回的 <c>/warmup</c>。
    /// </summary>
    private static TestHttpServer StartSlowBodyServer(TimeSpan bodyDelay)
        => TestHttpServer.Start(request => request.Path == "/warmup"
            ? TestJson.Response(new { ok = true })
            : new TestResponse(
                Body: Encoding.UTF8.GetBytes("{\"ok\":true}"),
                BodyDelay: bodyDelay));

    private sealed class WithIndexer
    {
        public string Name { get; set; } = string.Empty;

        public int Page { get; set; }

        public string this[int index] => "value-" + index;
    }
}
