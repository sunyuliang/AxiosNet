using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 全项目体检后的回归用例：每个用例对应一处已复现的缺陷，修复前后必须能区分。
/// <para>
/// 整类放进 <see cref="TimingCollection"/>：这里既有「必须在预算内被打断」的流式超时用例，
/// 又有同步读（会阻塞线程池线程）的用例。与其它集合并行跑时，线程池注入变慢会让
/// <b>正确</b>的实现超预算 —— GitHub Actions 上就出现过 500ms 预算在响应头阶段被吃光，
/// 用例还没读到正文就抛了 <c>timeout of 500ms exceeded</c>。
/// </para>
/// </summary>
[Collection(TimingCollection.Name)]
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
        await using var server = StartSlowBodyServer(SlowBodyDelay);

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
        await using var server = StartSlowBodyServer(SlowBodyDelay);

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
    /// <para>
    /// 这条用例曾经是「取消与正文到达赛跑」：正文只延迟 800ms，取消用 <c>CancelAfter(150)</c>。
    /// 两者都靠线程池上的计时器，CI 满载时回调可能晚到数秒（实测记录见 <c>StreamCancellationTests</c>），
    /// 谁先到全看排队 —— 正文先到，Read 就正常读到数据，用例报「没有抛异常」，
    /// 看起来像取消处理失效，实际只是调度抖动。
    /// </para>
    /// <para>
    /// 现在两头都拆掉计时器：正文延迟放大到 <see cref="SlowBodyDelay"/>（与
    /// <c>StreamRead_HonorsExternalCancellation</c> 同一口径），
    /// 取消改由独立线程触发 —— <see cref="Thread.Sleep(int)"/> + <see cref="CancellationTokenSource.Cancel()"/>
    /// 不排线程池队列，也不会晚到。读早于取消就阻塞被打断，晚于取消就撞上已取消的令牌，
    /// 两条路径都是 <c>ERR_CANCELED</c>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task StreamExternalCancel_InterruptsBlockedSyncRead()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"ok\":true}"),
            BodyDelay: SlowBodyDelay));

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = TimeSpan.FromSeconds(30),
            cts.Token);

        await using var stream = response.Data!;

        var canceller = new Thread(() =>
        {
            Thread.Sleep(150);
            cts.Cancel();
        })
        {
            IsBackground = true
        };
        canceller.Start();

        try
        {
            var ex = await Task.Run(() => Assert.Throws<AxiosException>(
                () => stream.Read(new byte[16], 0, 16)));

            Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        }
        finally
        {
            // 取消线程必须收尾：否则 cts 可能在取消进行中被释放
            canceller.Join();
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

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>
    /// 曾经是 500ms vs 3s：本地测试一直绿，但 GitHub Actions 满载时响应头阶段的调度抖动就能吃掉 500ms，
    /// 于是 <c>GetAsync</c> 先抛 <c>timeout of 500ms exceeded</c>，看起来像正文超时失效。
    /// 现在与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。
    /// </para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);

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
