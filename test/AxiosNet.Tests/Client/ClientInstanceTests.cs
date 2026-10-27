
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 实例生命周期与实例级选项：创建、释放、在途请求、只在实例级生效的连接选项。
/// </summary>
public sealed class ClientInstanceTests
{
    [Fact]
    public async Task RequestAsync_PerRequestWithCredentials_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    WithCredentials = true,
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestProxy_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    Proxy = new WebProxy("http://127.0.0.1:1"),
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestPoolOptions_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    PoolOptions = new ConnectionPoolOptions { MaxConnectionsPerServer = 1 },
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    // ===============================================================
    // Bug 13：自建池分支对重复传入的连接级选项静默取一个
    // ===============================================================

    [Fact]
    public void Constructor_PoolOptionsAndDefaultsPoolOptions_Throws()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig
            {
                Connection = new()
                {
                    PoolOptions = new ConnectionPoolOptions(),
                },
            },
            poolOptions: new ConnectionPoolOptions()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("poolOptions", ex.Message);
    }

    [Fact]
    public void Constructor_ProxyOnBothPoolOptionsAndDefaults_Throws()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig
            {
                Connection = new()
                {
                    Proxy = new WebProxy("http://127.0.0.1:9"),
                },
            },
            poolOptions: new ConnectionPoolOptions { Proxy = new WebProxy("http://127.0.0.1:10") }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ===============================================================
    // M4：ConnectTimeout 超过 SocketsHttpHandler 上限时必须走统一错误模型
    // ===============================================================

    [Fact]
    public void M4_ConnectTimeout_BeyondHandlerLimit_ThrowsAxiosException()
    {
        var limit = TimeSpan.FromMilliseconds(int.MaxValue);

        // 前提核实：池侧据以判定的上限必须与底层 Handler 的真实上限一致 —— 含端点可用、+1 毫秒即抛。
        // 上限是 BCL 常量，故当场用探针取证（而不是把常量抄进断言；将来 BCL 放宽会立刻暴露）。
        using (var probe = new SocketsHttpHandler())
        {
            probe.ConnectTimeout = limit;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => probe.ConnectTimeout = limit + TimeSpan.FromMilliseconds(1));
        }

        // 端点本身是合法值：池不能比 BCL 更严（多拦 1 毫秒 = 让原本可用的配置在升级后报错）
        using (var accepted = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" },
            poolOptions: new ConnectionPoolOptions { ConnectTimeout = limit }))
        {
            Assert.Equal(limit, accepted.Pool!.Options.ConnectTimeout);
        }

        // 越界值必须在构造期就以统一错误模型拦下，而不是等首次租用连接时漏出裸 BCL 异常（那时连 Config 都没有）
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" },
            poolOptions: new ConnectionPoolOptions { ConnectTimeout = TimeSpan.FromDays(30) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("ConnectTimeout", ex.Message);
    }

    // ------------------------------------------------------------------
    // 构造后改 Defaults：校验以池为准
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_RepeatsPoolValue_AfterDefaultsMutated_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 构造后改 defaults：不生效，池仍是 UseCookies = false
        client.Defaults.Connection.WithCredentials = true;
        Assert.False(client.Pool!.Options.UseCookies);

        // 按请求复述池的真实值 false —— 必须放行。
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_RepeatsMutatedDefaults_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Defaults.Connection.WithCredentials = true; // 未生效

        // 按请求写 true 等于「要求打开 Cookie 容器」，但池没开 —— 必须响亮报错
        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);

        // 文案报的「实例生效值」必须是池的真实值（False），不是 Defaults 上的 True
        Assert.Contains("实例生效值为 False", error.Message);
    }

    [Fact]
    public async Task PerRequestWithCredentials_Omitted_AfterDefaultsMutated_DoesNotThrow()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Defaults.Connection.WithCredentials = true;

        // 构造后改 defaults 的承诺是「静默不生效」，不是「每个请求都炸」
        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // poolOptions 打开 Cookie：Defaults 上看不出来，只有池知道
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_MatchesPoolOptionsUseCookies_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { UseCookies = true });

        Assert.True(client.Pool!.Options.UseCookies);

        // defaults 没写 WithCredentials（读出来是 false），但池的生效值是 true
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_DiffersFromPoolOptionsUseCookies_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { UseCookies = true });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
        Assert.Contains("实例生效值为 True", error.Message);
    }

    // ------------------------------------------------------------------
    // 共享池：Cookie 容器由池决定，与本实例 defaults 无关
    // ------------------------------------------------------------------

    [Fact]
    public async Task SharedPool_PerRequestWithCredentials_RepeatsPoolValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { UseCookies = true });
        using var client = new AxiosClient(pool: pool);

        // 共享池场景 defaults 上不能写连接级选项（构造期就抛），所以只能复述池的值
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SharedPool_PerRequestWithCredentials_DiffersFromPoolValue_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();
        using var client = new AxiosClient(pool: pool);

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 回归：构造期设置照旧生效（口径改成池值后不能把这条弄丢）
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_EqualToConstructionTimeValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new() { WithCredentials = true },
        });

        Assert.True(client.Pool!.Options.UseCookies);

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
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

    [Fact]
    public async Task Create_BuildsIndependentInstanceWithDefaults()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));

        var client = Axios.Create(c =>
        {
            c.BaseAddress = server.Url;
            c.Headers.Set("X-Trace", "instance-default");
        });

        using (client)
        {
            var response = await client.GetAsync<JsonElement>("anything");
            Assert.Equal("instance-default", response.Data.GetProperty("trace").GetString());
            Assert.Equal("/anything", server.Received[0].Path);
        }
    }

    [Fact]
    public async Task RequestAsync_ActionConfigured_GoesThroughSamePipeline()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { echo = req.Headers.GetValueOrDefault("x-custom") }));

        using var client = new AxiosClient();
        var response = await client.RequestAsync<JsonElement>(c =>
        {
            c.Url = server.Url + "explicit";
            c.Method = HttpMethod.Get;
            c.Headers.Set("X-Custom", "yes");
        });

        Assert.Equal("yes", response.Data.GetProperty("echo").GetString());
    }

    // ---------------------------------------------------------------
    // 快捷方法直接在 Defaults 的副本上填写（见 AxiosClient.Shortcut）：
    // 必须保证「请求之间」「请求与实例默认值之间」没有共享可变状态。
    // ---------------------------------------------------------------

    [Fact]
    public async Task ShortcutConfig_SequentialRequests_DoNotLeakIntoDefaultsOrEachOther()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Defaults.Headers.Set("X-Default", "base");
        client.Defaults.Headers.Set("X-Second", "2");

        // 第一次：加一个只属于本次的头，并摘掉一个实例默认头
        await client.GetAsync("first", c =>
        {
            c.Headers.Set("X-Only", "1");
            c.RemoveHeaders.Add("X-Second");
        });

        // 第二次：什么都不写，必须回到「只有实例默认头」的状态
        await client.GetAsync("second");

        var first = server.Received[0];
        var second = server.Received[1];

        // 本次生效
        Assert.Equal("1", first.Headers["X-Only"]);
        Assert.Equal("base", first.Headers["X-Default"]);
        Assert.False(first.Headers.ContainsKey("X-Second"));

        // 下一次不受影响：写的头不残留、删的头也没被写回实例默认值
        Assert.False(second.Headers.ContainsKey("X-Only"));
        Assert.Equal("2", second.Headers["X-Second"]);

        // 实例默认值原样
        Assert.Equal(2, client.Defaults.Headers.Count);
        Assert.Equal("base", client.Defaults.Headers.Get("X-Default"));
        Assert.Equal("2", client.Defaults.Headers.Get("X-Second"));
        Assert.Empty(client.Defaults.RemoveHeaders);
    }

    [Fact]
    public async Task ShortcutConfig_ConcurrentRequests_KeepTheirOwnHeaderValues()
    {
        // 服务端把收到的 X-Req 原样回显：任何一次请求的配置被别的请求覆盖，断言当场就会失败
        await using var server = TestHttpServer.Start(req => new TestResponse(Body: Encoding.UTF8.GetBytes(req.Headers["X-Req"]), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Defaults.Headers.Set("X-Default", "base");

        var calls = Enumerable.Range(0, 64).Select(async i =>
        {
            var tag = i.ToString(CultureInfo.InvariantCulture);
            var response = await client.GetAsync<string>("echo", c => c.Headers.Set("X-Req", tag));
            Assert.Equal(tag, response.Data);
        });

        await Task.WhenAll(calls);

        // 并发写完，实例默认值里也不该多出这次的头
        Assert.False(client.Defaults.Headers.Has("X-Req"));
        Assert.Equal("base", client.Defaults.Headers.Get("X-Default"));
    }

    // ---------------------------------------------------------------
    // Dispose / DisposeAsync 幂等
    // ---------------------------------------------------------------

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var client = new AxiosClient();

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var client = new AxiosClient();

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RequestAfterDisposeAsync_ThrowsCanceled()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var client = new AxiosClient();
        await client.DisposeAsync();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    [Fact]
    public async Task PerRequestWithCredentials_EqualToInstanceDefault_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 实例没开 WithCredentials（false），按请求写 false 是等价写法
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_DiffersFromInstance_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task InjectedHandler_PerRequestWithCredentials_AlwaysThrows()
    {
        // 注入 Handler 时无从得知它的 Cookie 策略，无法判断「是否等价」，即使写 false 也报错
        using var client = new AxiosClient(handler: new HttpClientHandler());

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ------------------------------------------------------------------
    // 上面的判定依赖「按请求到底写没写」的 *Set 标记：
    // perCall 只含按请求项，缺省值是 WithCredentials=false / MaxRedirects=5，
    // 若改成纯值比较，下面这两条「按请求根本没写」的正常请求会被误判成写了不同的值。
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestOptions_Omitted_WithCredentialsTrue_Instance_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestOptions_Omitted_PoolRedirectLimit3_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    // Dispose 不能打断在途请求

    [Fact]
    public async Task Dispose_WithRequestInFlight_LetsItComplete()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(400);
            return TestJson.Response(new { ok = true });
        });

        var client = new AxiosClient();
        var pending = client.GetAsync(server.Url + "slow");
        await WaitUntilAsync(() => server.Received.Count > 0);

        client.Dispose();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task PerRequestProxy_ThrowsBadOptionValue()
    {
        await using var server = TestJsonServer();
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "x",
            c => c.Connection.Proxy = new System.Net.WebProxy("http://127.0.0.1:9")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ------------------------------------------------------------------
    // L14/L15：关闭语义（不掐断在途请求；竞态不漏裸 ObjectDisposedException）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispose_DoesNotInterruptInFlightRequest_InjectedHandler()
    {
        // 注入 handler 的实例不应随 HttpClient.Dispose 取消在途请求。
        var gate = new TaskCompletionSource();
        using var handler = new GatedHandler(gate);
        using var client = new AxiosClient(handler: handler);

        var pending = client.GetAsync("http://axiosnet.test/x");
        await Task.Delay(100);
        client.Dispose();
        gate.SetResult();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Dispose_DoesNotInterruptInFlightRequest_WithOwnedPool()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: TestJson.Bytes(new { ok = true }),
            BodyDelay: TimeSpan.FromMilliseconds(600)));
        using var client = new AxiosClient();

        var pending = client.GetAsync(server.Url + "slow");
        await Task.Delay(120);
        client.Dispose();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task RequestAfterClientDispose_ThrowsCanceled_NotRawObjectDisposed()
    {
        using var client = new AxiosClient();
        client.Dispose();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/x"));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
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

    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


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


    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待测试服务器收到请求超时。");
            }

            await Task.Delay(10);
        }
    }


    private static TestHttpServer TestJsonServer()
        => TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

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