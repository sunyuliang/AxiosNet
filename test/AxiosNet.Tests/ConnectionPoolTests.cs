using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 额外实现的网络连接池能力验证：
/// TCP 连接复用、最大连接数限流、跨实例共享池、空闲驱逐、租约统计。
/// </summary>
public sealed class ConnectionPoolTests
{
    [Fact]
    public async Task SequentialRequests_ReuseSingleTcpConnection()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + "a");
        await client.GetAsync(server.Url + "b");

        // 关键性能指标：两个请求只建立了一条 TCP 连接，第二个请求在同一连接上复用
        Assert.Equal(1, server.TotalConnections);
        var perConnection = Assert.Single(server.RequestsPerConnection);
        Assert.Equal(2, perConnection.Value);

        var stats = client.Pool!.GetStatistics();
        Assert.Equal(1, stats.EndpointCount);
        Assert.Equal(2, stats.TotalLeases);
        Assert.All(stats.Endpoints, e => Assert.Equal(0, e.ActiveLeases));
    }

    [Fact]
    public async Task ConcurrentRequests_AreCappedByMaxConnectionsPerServer()
    {
        const int maxConnections = 2;
        const int requestCount = 6;

        await using var server = TestHttpServer.Start(async req =>
        {
            if (req.Path == "/slow")
            {
                await Task.Delay(300);
            }

            return TestJson.Response(new { ok = true });
        });

        var poolOptions = new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = maxConnections,
            // 测试期间不做后台驱逐
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        };

        using var client = new AxiosClient(poolOptions: poolOptions);

        var tasks = Enumerable.Range(0, requestCount)
            .Select(_ => client.GetAsync(server.Url + "slow"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(maxConnections, server.TotalConnections);
        Assert.Equal(maxConnections, server.PeakConcurrentRequests);
        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task SamePool_IsSharedAcrossMultipleAxiosClients()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });
        using var client1 = new AxiosClient(pool: pool);
        using var client2 = new AxiosClient(pool: pool);

        await client1.GetAsync(server.Url + "from-client-1");
        await client2.GetAsync(server.Url + "from-client-2");

        Assert.Equal(1, server.TotalConnections);
        var perConnection = Assert.Single(server.RequestsPerConnection);
        Assert.Equal(2, perConnection.Value);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
        Assert.Equal(2, pool.GetStatistics().TotalLeases);
    }

    [Fact]
    public async Task IdleOriginHandlers_AreEvictedInBackground()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var poolOptions = new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMilliseconds(200),
            EvictionInterval = TimeSpan.FromMilliseconds(80)
        };
        using var pool = new HttpConnectionPool(poolOptions);
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pool.GetStatistics().EndpointCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, pool.GetStatistics().EndpointCount);

        // 驱逐后再次请求可以透明地重建节点
        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal(200, response.Status);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task NewOrigin_GetsSeparatePooledHandler()
    {
        await using var server1 = TestHttpServer.Start(_ => TestJson.Response(new { which = 1 }));
        await using var server2 = TestHttpServer.Start(_ => TestJson.Response(new { which = 2 }));

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        await client.GetAsync(server1.Url);
        await client.GetAsync(server2.Url);
        await client.GetAsync(server1.Url);

        var stats = client.Pool!.GetStatistics();
        Assert.Equal(2, stats.EndpointCount);
        Assert.Equal(3, stats.TotalLeases);
        Assert.Contains(stats.Endpoints, e => e.Origin == $"http://127.0.0.1:{server1.Port}");
        Assert.Contains(stats.Endpoints, e => e.Origin == $"http://127.0.0.1:{server2.Port}");

        // server1 的 Handler 处理了两个请求
        Assert.Equal(1, server1.TotalConnections);
        Assert.Equal(2, server1.RequestsPerConnection.Values.Single());
    }

    [Fact]
    public async Task DisposeClient_WithOwnedPool_ReleasesHandlers()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });
        await client.GetAsync(server.Url);
        Assert.Equal(1, client.Pool!.GetStatistics().EndpointCount);

        client.Dispose();

        Assert.Equal(0, client.Pool!.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task DisposeClient_DoesNotDisposeExternallySharedPool()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var sharedPool = new HttpConnectionPool();

        var client = new AxiosClient(pool: sharedPool);
        await client.GetAsync(server.Url);
        client.Dispose();

        // 共享池仍然可用
        using var anotherClient = new AxiosClient(pool: sharedPool);
        var response = await anotherClient.GetAsync<JsonElement>(server.Url);
        Assert.Equal(200, response.Status);
    }
}
