
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 自定义 Handler：`DelegatingHandler` 套接、HandlerFactory 按 origin 缓存、与池选项互斥。
/// </summary>
public sealed class HandlerInjectionTests
{
    // ===============================================================
    // L10：HandlerFactory 拿到的 Options 不得是池的实时可变本体
    // ===============================================================

    [Fact]
    public void L10_HandlerFactory_DoesNotMutatePoolLiveOptions()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            EvictionInterval = TimeSpan.Zero,
            HandlerFactory = ctx =>
            {
                ctx.Options.MaxConnectionsPerServer = 7;
                return ctx.CreateDefaultHandler();
            }
        });

        using (pool.Lease(new Uri("http://127.0.0.1:9301/x")))
        {
        }

        Assert.Equal(100, pool.Options.MaxConnectionsPerServer);
    }

    // 直接注入单个 Handler（无连接池）

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

        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(poolOptions: new ConnectionPoolOptions(), handler: stub));

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

    // 连接池 HandlerFactory（保留池化的同时插入自定义 Handler）

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

        Assert.Equal(1, client.Pool!.GetStatistics().EndpointCount);
        Assert.Equal(1, server.TotalConnections);
    }

    [Fact]
    public async Task HandlerFactory_ReturnsIndependentHandlerPerOrigin()
    {
        // 工厂每次调用都要返回独立实例。
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

    // ------------------------------------------------------------------
    // L10：SslOptions 扩展点（回调 / 协议之外的 SslClientAuthenticationOptions）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigureSslOptions_ReachesHandlerWithoutOtherSslSettings()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        SslClientAuthenticationOptions? captured = null;
        SocketsHttpHandler? created = null;

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            // 只设扩展点：SslOptions 也要被构造。
            ConfigureSslOptions = options =>
            {
                options.EnabledSslProtocols = SslProtocols.Tls12;
                captured = options;
            },
            HandlerFactory = ctx =>
            {
                created = ctx.CreateDefaultHandler();
                return created;
            }
        });

        await client.GetAsync(server.Url + "x");

        Assert.NotNull(captured);
        Assert.Same(created!.SslOptions, captured);
        Assert.Equal(SslProtocols.Tls12, created.SslOptions!.EnabledSslProtocols);
    }

    [Fact]
    public void PoolOptions_Clone_CopiesConfigureSslOptions()
    {
        Action<SslClientAuthenticationOptions> configure = _ => { };
        var options = new ConnectionPoolOptions { ConfigureSslOptions = configure };

        Assert.Same(configure, options.Clone().ConfigureSslOptions);
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

    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}