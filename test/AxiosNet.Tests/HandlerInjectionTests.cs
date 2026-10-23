using System.Net;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// HttpMethod 直用 + 自定义 Handler 注入（单 Handler 模式 / 连接池 HandlerFactory）。
/// </summary>
public sealed class HandlerInjectionTests
{
    /// <summary>记录经过的请求，内层可接真实 SocketsHttpHandler。</summary>
    private sealed class TraceHandler : DelegatingHandler
    {
        private readonly List<(string Method, string Uri)> _requests = new();

        public IReadOnlyList<(string Method, string Uri)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int DisposeCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add((request.Method.Method, request.RequestUri!.ToString()));
            }

            return base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }

    /// <summary>完全离线的桩 Handler：不建连接，直接返回固定 JSON。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        public int Calls;
        public int DisposeCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    // HttpMethod（不再有自研枚举）
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomMethod_IsSentVerbatim()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { method = req.Method }));
        using var client = new AxiosClient();

        var response = await client.RequestAsync<JsonElement>(c =>
        {
            c.Url = server.Url + "resource";
            c.Method = new HttpMethod("PROPFIND");
        });

        Assert.Equal("PROPFIND", server.Received[0].Method);
        Assert.Equal("PROPFIND", response.Data.GetProperty("method").GetString());
    }

    [Fact]
    public void Method_Null_Throws()
    {
        var config = new AxiosConfig();

        Assert.Throws<ArgumentNullException>(() => config.Method = null!);
        Assert.Equal(HttpMethod.Get, config.Method);
    }

    [Fact]
    public void Method_DefaultsToGet_AndMarksAsSetOnlyWhenAssigned()
    {
        var defaults = new AxiosConfig();
        Assert.Equal(HttpMethod.Get, defaults.Method);

        // 用 RequestAsync 的配置体演示"按请求覆盖方法"
        var config = new AxiosConfig { Url = "https://example.com/" };
        config.Method = HttpMethod.Patch;

        Assert.Equal(HttpMethod.Patch, config.Method);
    }

    [Fact]
    public async Task PatchMethod_UsesBclHttpMethod_AndSendsBody()
    {
        await using var server = TestHttpServer.Start(req => new TestResponse(
            Body: Encoding.UTF8.GetBytes(req.Method),
            ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.PatchAsync(server.Url + "users/1", new { name = "bob" });

        Assert.Equal("PATCH", response.Data);
        Assert.Equal("PATCH", server.Received[0].Method);
    }

    // ------------------------------------------------------------------
    // 直接注入单个 Handler（无连接池）
    // ------------------------------------------------------------------

    [Fact]
    public async Task InjectedHandler_IsUsedForAllOrigins()
    {
        var stub = new StubHandler("""{"name":"bob"}""");
        using var client = new AxiosClient(handler: stub, disposeHandler: false);

        var first = await client.GetAsync<JsonElement>("https://a.example.com/users/1");
        var second = await client.GetAsync<JsonElement>("https://b.example.com/users/2");

        Assert.Equal(2, stub.Calls);
        Assert.Equal("bob", first.Data.GetProperty("name").GetString());
        Assert.Equal("bob", second.Data.GetProperty("name").GetString());
        Assert.False(client.UsesConnectionPool);
        Assert.Null(client.Pool);
    }

    [Fact]
    public async Task InjectedHandler_DelegatingHandlerChain_WorksWithRealTransport()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { ok = true }));

        var trace = new TraceHandler { InnerHandler = new SocketsHttpHandler() };
        using var client = new AxiosClient(handler: trace);

        var response = await client.GetAsync(server.Url + "ping");

        Assert.Equal(200, response.Status);
        var request = Assert.Single(trace.Requests);
        Assert.Equal("GET", request.Method);
        Assert.EndsWith("/ping", request.Uri);
    }

    [Fact]
    public async Task InjectedHandler_DisposeHandlerTrue_DisposesHandler()
    {
        var stub = new StubHandler("{}");
        var client = new AxiosClient(handler: stub);

        await client.GetAsync("https://example.com/");
        client.Dispose();

        Assert.Equal(1, stub.DisposeCount);
    }

    [Fact]
    public async Task InjectedHandler_DisposeHandlerFalse_LeavesHandlerAlive()
    {
        var stub = new StubHandler("{}");
        var client = new AxiosClient(handler: stub, disposeHandler: false);

        await client.GetAsync("https://example.com/");
        client.Dispose();

        Assert.Equal(0, stub.DisposeCount);
    }

    [Fact]
    public void InjectedHandler_WithPool_Throws()
    {
        using var pool = new HttpConnectionPool();
        using var stub = new StubHandler("{}");

        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(pool: pool, handler: stub));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("handler", ex.Message);
    }

    [Fact]
    public void InjectedHandler_WithPoolOptions_Throws()
    {
        using var stub = new StubHandler("{}");

        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(poolOptions: new ConnectionPoolOptions(), handler: stub));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void InjectedHandler_WithMaxRedirects_Throws()
    {
        using var stub = new StubHandler("{}");

        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            }, handler: stub));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("MaxRedirects", ex.Message);
    }

    [Fact]
    public void InjectedHandler_WithProxy_Throws()
    {
        using var stub = new StubHandler("{}");

        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    Proxy = new WebProxy("http://127.0.0.1:8888"),
                },
            }, handler: stub));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ------------------------------------------------------------------
    // 连接池 HandlerFactory（保留池化的同时插入自定义 Handler）
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandlerFactory_InvokedOncePerOrigin_AndKeepsConnectionReuse()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { ok = true }));

        var contexts = new List<PooledHandlerContext>();
        var traces = new List<TraceHandler>();

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 7,
            HandlerFactory = ctx =>
            {
                contexts.Add(ctx);
                var trace = new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() };
                traces.Add(trace);
                return trace;
            }
        });

        for (var i = 0; i < 3; i++)
        {
            await client.GetAsync(server.Url + "ping");
        }

        var context = Assert.Single(contexts);
        Assert.Equal($"http://127.0.0.1:{server.Port}", context.Origin);
        Assert.Equal(new Uri(server.Url), context.BaseAddress);
        Assert.Equal(7, context.Options.MaxConnectionsPerServer);

        var trace = Assert.Single(traces);
        Assert.Equal(3, trace.Requests.Count);

        // 仍是"按 origin 一个 Handler + 连接复用"，不是每个请求新建
        Assert.Equal(1, client.Pool!.GetStatistics().EndpointCount);
        Assert.Equal(1, server.TotalConnections);
    }

    [Fact]
    public async Task HandlerFactory_ReturnsIndependentHandlerPerOrigin()
    {
        // 契约：工厂每次调用都要给一个**独立**实例（见 README「HandlerFactory 约定」）。
        // 复用同一个 DelegatingHandler 的写法在单 origin 下能跑，多 origin 时后一次调用会把
        // 它的 InnerHandler 顶掉，前一个 origin 的请求随即挂在内层已被替换的实例上。
        await using var first = TestHttpServer.Start(req => TestJson.Response(new { origin = "first" }));
        await using var second = TestHttpServer.Start(req => TestJson.Response(new { origin = "second" }));

        var traces = new List<TraceHandler>();

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            HandlerFactory = ctx =>
            {
                var trace = new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() };
                traces.Add(trace);
                return trace;
            }
        });

        await client.GetAsync(first.Url + "ping");
        await client.GetAsync(second.Url + "ping");
        // 回到第一个 origin：它必须仍能正常发出请求（Handler 没被第二个 origin 抢走）
        await client.GetAsync(first.Url + "ping");

        Assert.Equal(2, traces.Count);
        Assert.NotSame(traces[0], traces[1]);
        Assert.Equal(2, traces[0].Requests.Count);
        Assert.Single(traces[1].Requests);
        Assert.Equal(2, client.Pool!.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task HandlerFactory_HandlersAreDisposedWhenPoolCleared()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { ok = true }));

        var trace = new TraceHandler { InnerHandler = new SocketsHttpHandler() };

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            HandlerFactory = _ => trace
        });

        await client.GetAsync(server.Url + "ping");
        Assert.Equal(0, trace.DisposeCount);

        client.Pool!.Clear();
        Assert.Equal(1, trace.DisposeCount);
    }

    [Fact]
    public async Task HandlerFactory_NullReturn_Throws()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            HandlerFactory = _ => null!
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(server.Url + "ping"));
        Assert.Contains("HandlerFactory", ex.Message);
    }

    [Fact]
    public async Task HandlerFactory_CanMutateDefaultHandler()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { ok = true }));

        SocketsHttpHandler? created = null;

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            HandlerFactory = ctx =>
            {
                created = ctx.CreateDefaultHandler();
                created.AutomaticDecompression = DecompressionMethods.None;
                return created;
            }
        });

        await client.GetAsync(server.Url + "ping");

        Assert.NotNull(created);
        Assert.Equal(DecompressionMethods.None, created!.AutomaticDecompression);
        Assert.Equal(100, created.MaxConnectionsPerServer);
    }

    [Fact]
    public void PoolOptions_Clone_CopiesHandlerFactory()
    {
        Func<PooledHandlerContext, HttpMessageHandler> factory = _ => new StubHandler("{}");
        var options = new ConnectionPoolOptions { HandlerFactory = factory };

        var clone = options.Clone();

        Assert.Same(factory, clone.HandlerFactory);
    }
}
