using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-20 全量排查的问题回归：错误模型收口、流式租约泄漏、状态码丢失、
/// 令牌重载陷阱、配置合并语义。
/// </summary>
public sealed class AuditRegressionTests
{
    [Fact]
    public async Task NonHttpScheme_ThrowsInvalidUrl()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("ftp://example.com/x"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    [Fact]
    public async Task RequestBodySerializationFailure_IsWrappedAsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var node = new Node();
        node.Self = node;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "x", node));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<JsonException>(ex.Cause);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task TransformRequestFailure_IsWrappedAsBadRequest()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            new { a = 1 },
            c => c.TransformRequest.Add((_, _) => throw new FormatException("bad transform"))));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<FormatException>(ex.Cause);
        Assert.Empty(server.Received);
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

    [Fact]
    public async Task ErrorStatus_WithUnparsableBody_KeepsStatusAndResponse()
    {
        const string html = "<html><body>Not Found</body></html>";
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            404,
            "Not Found",
            Encoding.UTF8.GetBytes(html),
            "text/html; charset=utf-8"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(server.Url + "missing"));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(404, ex.Status);
        Assert.NotNull(ex.Response);
        Assert.Equal(html, ex.Response!.Data);
    }

    [Fact]
    public async Task OkStatus_WithUnparsableBody_StillThrowsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            "OK",
            Encoding.UTF8.GetBytes("<html>not json</html>"),
            "text/html; charset=utf-8"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task CancellationTokenAsSecondArgument_IsNotSerializedAsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        await client.GetAsync(server.Url + "x", cts.Token);
        await client.PostAsync(server.Url + "x", cts.Token);

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, r => Assert.Empty(r.Body));
        Assert.Equal("GET", server.Received[0].Method);
        Assert.Equal("POST", server.Received[1].Method);
    }

    [Fact]
    public async Task CancellationTokenInsideObject_IsRejected()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://127.0.0.1:1/x", (object)new CancellationTokenSource().Token));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task EmptyHeaderValue_IsSent()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("X-Empty", string.Empty));

        Assert.True(server.Received[0].Headers.ContainsKey("X-Empty"));
        Assert.Equal(string.Empty, server.Received[0].Headers["X-Empty"]);
    }

    [Fact]
    public async Task RemoveHeaders_RemovesInstanceDefaultHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });

        await client.GetAsync(server.Url + "x", c => c.RemoveHeaders.Add("X-Trace"));

        Assert.False(server.Received[0].Headers.ContainsKey("X-Trace"));
    }

    [Fact]
    public async Task RemoveHeaders_ConcurrentMutation_DoesNotBreakInFlightRequests()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });

        using var stop = new CancellationTokenSource();
        var mutation = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                // 枚举 + 增删并发进行，快照语义不能抛 InvalidOperationException
                foreach (var _ in client.Defaults.RemoveHeaders)
                {
                }

                client.Defaults.RemoveHeaders.Add("X-Temp" + i++);
                client.Defaults.RemoveHeaders.Remove("X-Temp" + i);
            }
        });

        var requests = Enumerable.Range(0, 16)
            .Select(i => client.GetAsync(server.Url + "x" + i, c => c.RemoveHeaders.Add("X-Trace")))
            .ToArray();

        Assert.All(await Task.WhenAll(requests), r => Assert.Equal(200, r.Status));
        stop.Cancel();
        await mutation;
    }

    [Fact]
    public async Task HttpContentWithoutContentType_IsNotGivenJsonHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        await client.PostAsync(server.Url + "x", content);

        Assert.False(server.Received[0].Headers.ContainsKey("Content-Type"));
        Assert.Equal("hello", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task Multipart_WithContentTypeHeader_KeepsGeneratedBoundary()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { title = "t", file = FormFile.FromBytes("a.txt", new byte[] { 1, 2, 3 }) },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Headers.Set("Content-Type", "multipart/form-data");
            });

        var request = server.Received[0];
        Assert.Contains("boundary=", request.Headers["Content-Type"]);
        Assert.Contains("t", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public void InvalidPoolOption_FailsAtConstruction()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { MaxConnectionsPerServer = 0 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void ZeroMaxAutomaticRedirections_FailsAtConstruction()
    {
        // SocketsHttpHandler 对 <= 0 一律抛裸 ArgumentOutOfRangeException，必须在构造期拦住。
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { MaxAutomaticRedirections = 0 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task ZeroPooledConnectionLifetime_IsAllowedAndWorks()
    {
        // 0 是合法语义（连接不复用），不能被构造期校验拦掉。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url + "a");
        await client.GetAsync(server.Url + "b");

        // 0 的语义 = 连接不复用：两次请求各建一条 TCP 连接
        Assert.Equal(2, server.TotalConnections);
    }

    [Fact]
    public async Task Multipart_WithContentTypeHeaderAndProgressWrapper_KeepsBoundary()
    {
        // OnUploadProgress 会把 multipart 包进 ProgressHttpContent：boundary 保护必须穿透包装层。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var events = 0;
        await client.PostAsync(
            server.Url + "upload",
            new { title = "t", file = FormFile.FromBytes("a.txt", new byte[] { 1, 2, 3 }) },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Headers.Set("Content-Type", "multipart/form-data");
                c.Callbacks.OnUploadProgress = _ => events++;
            });

        Assert.Contains("boundary=", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("t", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.True(events > 0);
    }

    [Fact]
    public async Task PerRequestPoolOptions_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.PoolOptions = new ConnectionPoolOptions()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }
}
