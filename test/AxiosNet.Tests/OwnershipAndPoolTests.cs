using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 流式正文的所有权与连接池租约生命周期（高风险区）：
/// <list type="bullet">
/// <item>流式响应的连接租约必须活到流 Dispose 为止，且只在流 Dispose 时归还一次；</item>
/// <item>「客户端已 Dispose、下载还在继续」不能打断进行中的正文读取；</item>
/// <item>流式正文上的逐字节读取（<see cref="Stream.ReadByte"/>）同样受 maxContentLength 约束；</item>
/// <item>租约计数：重复 Dispose 只归还一次，池关闭/清空时归还不能崩、不能漏；</item>
/// <item>请求拦截器的 <c>OnRejected</c> 只兜自己的 <c>OnFulfilled</c>，不吞发送阶段的错误。</item>
/// </list>
/// </summary>
public sealed class OwnershipAndPoolTests
{
    private static HttpConnectionPool NewPool(int maxEndpointCount = 1024)
        => new(new ConnectionPoolOptions
        {
            MaxEndpointCount = maxEndpointCount,
            // 全程关闭后台驱逐线程，用例自己决定何时驱逐
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

    // ---------------------------------------------------------------
    // 流式正文：逐字节读取仍受长度上限约束
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ReadByte_EnforcesMaxContentLength()
    {
        // chunked：服务端不声明长度，上限只能靠「边读边计数」生效
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[64],
            ContentType: "application/octet-stream",
            Chunked: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "chunked", c => c.MaxContentLength = 4);
        await using var stream = response.Data!;

        // ReadByte 不覆写，靠基类换算成 Read(byte[],int,int)：必须落到同一条计数路径上
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, stream.ReadByte());
        }

        var ex = Assert.Throws<AxiosException>(() => { stream.ReadByte(); });
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
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

        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: payload,
            ContentType: "application/octet-stream"));

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
    // 池本身：租约计数、关闭竞态、容量
    // ---------------------------------------------------------------

    [Fact]
    public void PoolLease_DoubleDispose_ReturnsOnlyOnce()
    {
        using var pool = NewPool();
        var lease = pool.Lease(new Uri("http://127.0.0.1:9000/x"));

        lease.Dispose();
        lease.Dispose();

        var stats = pool.GetStatistics();
        Assert.Equal(1, stats.TotalLeases);
        Assert.All(stats.Endpoints, e => Assert.Equal(0, e.ActiveLeases));

        // 计数没有被减成负数：空闲节点照常可驱逐
        Assert.Equal(1, pool.EvictIdleEndpoints());
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Pool_ClearWhileLeased_LeaseReturnDoesNotThrowAndPoolRecovers()
    {
        using var pool = NewPool();

        var lease = pool.Lease(new Uri("http://127.0.0.1:9000/x"));
        pool.Clear();

        Assert.Equal(0, pool.GetStatistics().EndpointCount);

        // 节点已被 Clear 释放，归还这条租约必须安全（不能 NRE / 重复 Dispose）
        lease.Dispose();
        Assert.Equal(0, pool.GetStatistics().EndpointCount);

        // 同一个 origin 可以透明重建
        using var again = pool.Lease(new Uri("http://127.0.0.1:9000/x"));
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Pool_EvictIdleEndpoints_SkipsActiveLeases()
    {
        using var pool = NewPool();
        var lease = pool.Lease(new Uri("http://127.0.0.1:9000/x"));

        // IdleHandlerLifetime = 0：只要没人在用就该被驱逐；有租约时必须跳过
        Assert.Equal(0, pool.EvictIdleEndpoints());
        Assert.Equal(1, pool.GetStatistics().EndpointCount);

        lease.Dispose();

        Assert.Equal(1, pool.EvictIdleEndpoints());
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public void Pool_MaxEndpointCountZero_MeansUnlimited()
    {
        using var pool = NewPool(maxEndpointCount: 0);

        var leases = new List<PoolLease>();
        for (var i = 0; i < 8; i++)
        {
            leases.Add(pool.Lease(new Uri($"http://127.0.0.1:{9100 + i}/x")));
        }

        Assert.Equal(8, pool.GetStatistics().EndpointCount);

        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    // ---------------------------------------------------------------
    // 请求拦截器的 rejected 语义（与 axios 的有意分歧）
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestInterceptor_OnRejected_DoesNotSwallowSendStageErrors()
    {
        using var client = new AxiosClient();
        var rejected = false;

        client.Interceptors.Request.Use(
            config => Task.FromResult(config),
            ex =>
            {
                rejected = true;
                return Task.FromResult(new AxiosConfig());
            });

        // 未被监听的端口：错误发生在发送阶段，绝不能进请求拦截器的 rejected
        // （axios 里这会变成「拿返回的 config 再发一次请求」，只做日志的 rejected 会静默重发 POST）
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/unreachable", c => c.Timeout = TimeSpan.FromSeconds(5)));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.False(rejected);
    }

    [Fact]
    public async Task RequestInterceptor_OnRejected_RecoversOnlyItsOwnOnFulfilledFailure()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        var recovered = false;

        client.Interceptors.Request.Use(
            _ => throw new InvalidOperationException("token expired"),
            ex =>
            {
                Assert.IsType<InvalidOperationException>(ex);
                recovered = true;
                return Task.FromResult(new AxiosConfig { Url = server.Url + "retry" });
            });

        var response = await client.GetAsync(server.Url + "original");

        Assert.Equal(200, response.Status);
        Assert.True(recovered);

        // 原始请求一次都没发出去，rejected 返回的 config 被当成「再发一次」
        var sent = Assert.Single(server.Received);
        Assert.Equal("/retry", sent.Path);
        Assert.Equal("GET", sent.Method);
    }
}
