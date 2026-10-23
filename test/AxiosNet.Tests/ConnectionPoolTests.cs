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

    // ---------------------------------------------------------------
    // 共享池的 fail-fast：连接级实例配置不能静默忽略
    // ---------------------------------------------------------------

    [Fact]
    public void SharedPool_WithInstanceLevelConnectionOptions_Throws()
    {
        using var pool = new HttpConnectionPool();

        Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            }, pool: pool));
        Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    WithCredentials = true,
                },
            }, pool: pool));
        Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    Proxy = new System.Net.WebProxy("http://127.0.0.1:9"),
                },
            }, pool: pool));
        Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    Credentials = System.Net.CredentialCache.DefaultCredentials,
                },
            }, pool: pool));
        Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    PoolOptions = new ConnectionPoolOptions(),
                },
            }, pool: pool));
    }

    [Fact]
    public void SharedPool_WithPoolOptionsParameter_ThrowsBadOptionValue()
    {
        using var pool = new HttpConnectionPool();

        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(pool: pool, poolOptions: new ConnectionPoolOptions()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("poolOptions", ex.Message);
    }

    [Fact]
    public void SharedPool_MaxRedirectsError_MentionsPoolOptionsGuidance()
    {
        using var pool = new HttpConnectionPool();

        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(new AxiosConfig
            {
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            }, pool: pool));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("MaxRedirects", ex.Message);
        Assert.Contains("ConnectionPoolOptions", ex.Message);
    }

    [Fact]
    public void SharedPool_WithCleanDefaults_Works()
    {
        using var pool = new HttpConnectionPool();

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:1" }, pool: pool);

        Assert.Same(pool, client.Pool);
        Assert.True(client.UsesConnectionPool);
    }

    [Fact]
    public async Task SharedPool_RedirectBehaviorComesFromPoolOptions()
    {
        await using var server = StartRedirectServer();

        // 池上 AllowAutoRedirect=false：共享该池的 client 不跟随重定向 —— 连接行为由池决定
        using var noFollowPool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            AllowAutoRedirect = false,
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });
        using var noFollowClient = new AxiosClient(pool: noFollowPool);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => noFollowClient.GetAsync(server.Url + "redir"));
        Assert.Equal(302, ex.Status);
        Assert.DoesNotContain(server.Received, r => r.Path == "/final");

        // 默认共享池跟随重定向，且共享 client 的 defaults 保持干净
        using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });
        using var client = new AxiosClient(pool: sharedPool);

        var response = await client.GetAsync(server.Url + "redir");
        Assert.Equal(200, response.Status);
        Assert.Contains(server.Received, r => r.Path == "/final");
    }

    private static TestHttpServer StartRedirectServer()
        => TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                302,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new { ok = true }),
            _ => new TestResponse(404)
        });

    // ---------------------------------------------------------------
    // origin key 规范化：IDNA 归一化，但不合并 scheme/port 不同的目标
    // ---------------------------------------------------------------

    [Fact]
    public void OriginKey_NormalizesUnicodeHostToPunycode()
    {
        // münchen.de 与 xn--mnchen-3ya.de 是同一个主机名的两种书写：
        // HTTP Host 头只能是 ASCII，发送前 unicode 形式会被规范化成 punycode，
        // 服务端看到的两者字节完全相同 —— 池 key 也必须归一，否则同一站点分裂成两个节点。
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        using var lease1 = pool.Lease(new Uri("http://münchen.de/ping"));
        using var lease2 = pool.Lease(new Uri("http://xn--mnchen-3ya.de/ping"));

        Assert.Equal("http://xn--mnchen-3ya.de:80", lease1.Origin);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
        Assert.Equal(2, pool.GetStatistics().TotalLeases);
    }

    [Fact]
    public void OriginKey_KeepsIpv6BracketsAndSeparatesSchemeAndPort()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        using var ipv6 = pool.Lease(new Uri("http://[::1]:8080/ping"));
        Assert.Equal("http://[::1]:8080", ipv6.Origin);

        // IPv4、不同端口、不同 scheme 仍是不同的 origin（按 scheme://host:port 隔离）
        using var ipv4 = pool.Lease(new Uri("http://127.0.0.1:80/ping"));
        Assert.Equal("http://127.0.0.1:80", ipv4.Origin);

        using var otherPort = pool.Lease(new Uri("http://127.0.0.1:81/ping"));
        Assert.Equal("http://127.0.0.1:81", otherPort.Origin);

        using var tls = pool.Lease(new Uri("https://127.0.0.1:443/ping"));
        Assert.Equal("https://127.0.0.1:443", tls.Origin);

        Assert.Equal(4, pool.GetStatistics().EndpointCount);
    }

    // ---------------------------------------------------------------
    // 选项隔离：池构造时克隆，调用方事后改动不影响池；Options 返回副本
    // ---------------------------------------------------------------

    [Fact]
    public void PoolOptions_AreIsolatedFromCallerMutation()
    {
        var options = new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 32,
            IdleHandlerLifetime = TimeSpan.FromMinutes(30),
            Proxy = new System.Net.WebProxy("http://127.0.0.1:8888")
        };

        using var pool = new HttpConnectionPool(options);

        // 1) 调用方事后修改自己持有的 options 对象，不影响已建成的池
        options.MaxConnectionsPerServer = 1;
        options.IdleHandlerLifetime = TimeSpan.Zero;
        options.Proxy = null;

        Assert.Equal(32, pool.Options.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromMinutes(30), pool.Options.IdleHandlerLifetime);
        Assert.NotNull(pool.Options.Proxy);

        // 2) 修改 Options 返回的副本，同样不影响池
        pool.Options.MaxConnectionsPerServer = 7;
        Assert.Equal(32, pool.Options.MaxConnectionsPerServer);
    }
}
