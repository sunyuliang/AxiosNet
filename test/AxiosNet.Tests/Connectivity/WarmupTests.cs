using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 预热：<see cref="HttpConnectionPool.Prewarm"/> 预建 origin 节点但不建 TCP，
/// <see cref="AxiosClient.Warmup"/> 的一次性标记、基地址解析与「不预连」语义。
/// </summary>
public sealed class WarmupTests
{
// 池：Prewarm 建节点、不建连

    [Fact]
    public void Prewarm_BuildsOriginNodeWithoutConnecting()
    {
        // 不可达地址最能说明「只建节点、不建连」：真去连必然失败。
        using var pool = new HttpConnectionPool();

        pool.Prewarm(new Uri("http://127.0.0.1:1/"));

        var stats = pool.GetStatistics();
        Assert.Equal(1, stats.EndpointCount);
        Assert.Equal(0, stats.TotalLeases);
        Assert.Equal(0, stats.Endpoints[0].ActiveLeases);
    }

    [Fact]
    public async Task Prewarm_NodeIsReusedByFirstRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("prewarm", 1)));
        using var pool = new HttpConnectionPool();
        using var client = new AxiosClient(pool: pool);

        pool.Prewarm(new Uri(server.Url));

        // 预热阶段一个 TCP 都不该有：建连留给第一条真正的请求。
        Assert.Equal(0, server.TotalConnections);

        await client.GetAsync(server.Url + "first");

        // 节点是复用的：请求只多了那条 TCP，没有多出一个 origin 节点。
        Assert.Equal(1, server.TotalConnections);
        var stats = pool.GetStatistics();
        Assert.Equal(1, stats.EndpointCount);
        Assert.Equal(1, stats.TotalLeases);
    }

    [Fact]
    public void Prewarm_Twice_KeepsSingleNode()
    {
        using var pool = new HttpConnectionPool();
        var uri = new Uri("http://127.0.0.1:9201/");

        pool.Prewarm(uri);
        pool.Prewarm(uri);

        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Prewarm_NamedTransport_CreatesSeparateNode()
    {
        using var pool = new HttpConnectionPool(
            new ConnectionPoolOptions().AddTransport("vpn", new ConnectionTransportOptions()));

        var uri = new Uri("http://127.0.0.1:9202/");
        pool.Prewarm(uri);
        pool.Prewarm(uri, "vpn");

        // origin 的粒度是 (scheme, host, port, 传输档)：两个档各自一个节点。
        var stats = pool.GetStatistics();
        Assert.Equal(2, stats.EndpointCount);
        Assert.Contains(stats.Endpoints, e => e.Transport.Length == 0);
        Assert.Contains(stats.Endpoints, e => e.Transport == "vpn");
    }

    [Fact]
    public void Prewarm_RelativeUri_ThrowsBadOption()
    {
        using var pool = new HttpConnectionPool();

        var error = Assert.Throws<AxiosException>(() => pool.Prewarm(new Uri("/relative", UriKind.Relative)));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public void Prewarm_UnregisteredTransport_ThrowsBadOption()
    {
        using var pool = new HttpConnectionPool();

        var error = Assert.Throws<AxiosException>(() => pool.Prewarm(new Uri("http://127.0.0.1:9203/"), "vpn"));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public void Prewarm_AfterDispose_ThrowsObjectDisposed()
    {
        var pool = new HttpConnectionPool();
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Prewarm(new Uri("http://127.0.0.1:9204/")));
    }

// 实例：Warmup 只做一次、按基地址建节点、失败不污染标记

    [Fact]
    public void Warmup_WithoutBaseAddress_OnlyRunsStaticWarmup()
    {
        using var client = new AxiosClient();

        Assert.True(client.Warmup());
        Assert.Equal(0, client.Pool!.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Warmup_WithBaseAddress_BuildsNodeOnceThenReturnsFalse()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:9205/" });

        Assert.True(client.Warmup());
        Assert.False(client.Warmup());
        Assert.Equal(1, client.Pool!.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task Warmup_ThenFirstRequest_ConnectsOnceAndReusesNode()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("warm", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        Assert.True(client.Warmup());
        Assert.Equal(0, server.TotalConnections);

        await client.GetAsync("first");

        Assert.Equal(1, server.TotalConnections);
        Assert.Equal(1, client.Pool!.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Warmup_RelativeBaseAddress_ThrowsAndKeepsInstanceUnwarmed()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "api/v1" });

        var error = Assert.Throws<AxiosException>(() => client.Warmup());
        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);

        // 标记不能被这次失败污染：再调一次仍然明确报错，而不是静默返回 false。
        Assert.Throws<AxiosException>(() => client.Warmup());
    }

    [Fact]
    public void Warmup_PoolPrewarmFails_KeepsInstanceUnwarmed()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { MaxEndpointCount = 1 });
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:9206/" }, pool: pool);

        // 占掉唯一的节点名额：预热必须新建第二个 origin 节点，容量不足。
        var lease = pool.Lease(new Uri("http://127.0.0.1:9207/"));

        var error = Assert.Throws<AxiosException>(() => client.Warmup());
        Assert.Equal(AxiosException.ErrPoolExhausted, error.Code);

        // 标记不能被这次失败污染：再调仍然明确报错，而不是静默返回 false。
        Assert.Throws<AxiosException>(() => client.Warmup());

        // 容量恢复后必须真的能预热成功：回滚的是「已完成」标记，不是把实例焊死成永久失败态。
        lease.Dispose();
        pool.Clear();
        Assert.True(client.Warmup());
        Assert.False(client.Warmup());
    }

    [Fact]
    public void Warmup_WithInjectedHandler_SkipsPoolPrewarm()
    {
        using var handler = new NoopHandler();
        using var client = new AxiosClient(handler: handler);

        Assert.True(client.Warmup());
        Assert.Null(client.Pool);
    }

    [Fact]
    public void Warmup_AfterDispose_ThrowsCanceled()
    {
        var client = new AxiosClient();
        client.Dispose();

        var error = Assert.Throws<AxiosException>(() => client.Warmup());

        // 与请求同一口径：释放过的实例不做事，也不漏裸 ObjectDisposedException。
        Assert.Equal(AxiosException.ErrCanceled, error.Code);
    }

    /// <summary>注入 Handler 分支用的空转 Handler：预热不该碰它，也不需要它真的发请求。</summary>
    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
