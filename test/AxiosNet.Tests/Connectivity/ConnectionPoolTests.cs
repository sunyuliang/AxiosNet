
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 连接池：按 origin 复用、容量与驱逐、共享池、租约释放、统计自洽。
/// </summary>
public sealed class ConnectionPoolTests
{
    // 连接池 Dispose 在锁外释放 Handler。
    [Fact]
    public async Task Pool_Dispose_ReleasesHandlerOutsideLock_StatisticsNotBlocked()
    {
        var disposeEntered = new ManualResetEventSlim(false);
        var releaseDispose = new ManualResetEventSlim(false);
        var handler = new BlockingDisposeHandler(disposeEntered, releaseDispose);

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            HandlerFactory = _ => handler,
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        using (pool.Lease(new Uri("http://127.0.0.1:9500/x")))
        {
        }

        var disposeTask = Task.Run(() => pool.Dispose());
        var statsTask = Task.Run(() => pool.GetStatistics());

        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)), "Dispose 没有进入 Handler 释放。");

        // 锁内只做判定，释放移到锁外，统计不被拖住。
        var statsCompleted = await Task.WhenAny(statsTask, Task.Delay(TimeSpan.FromSeconds(5))) == statsTask;
        Assert.True(statsCompleted, "Dispose 持锁释放 Handler：GetStatistics 被拖住（应锁内判定、锁外释放）。");

        releaseDispose.Set();
        var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))) == disposeTask;
        Assert.True(disposeCompleted, "Dispose 未在放行后完成。");
        Assert.True(handler.Disposed);
    }

    // EvictionInterval 越界归一为 AxiosException。
    [Fact]
    public void EvictionInterval_BeyondTimerLimit_ThrowsAxiosException()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromDays(50) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void EvictionInterval_Negative_ThrowsAxiosException()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromSeconds(-1) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void EvictionInterval_Zero_DoesNotThrow()
    {
        // 0 = 不启用后台定时器。
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });
        Assert.NotNull(pool);
    }

    [Fact]
    public void EvictionInterval_JustUnderTimerLimit_DoesNotThrow()
    {
        // 49 天 < 约 49.7 天的 Timer 上限。
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromDays(49) });
        Assert.NotNull(pool);
    }

    // ===============================================================
    // Bug 10：GetStatistics 的 EndpointCount 与 Endpoints 快照不一致
    // ===============================================================

    [Fact]
    public void GetStatistics_EndpointCount_EqualsEndpointsArrayLength()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions());

        using var lease1 = pool.Lease(new Uri("http://127.0.0.1:9101/x"));
        using var lease2 = pool.Lease(new Uri("http://127.0.0.1:9102/x"));

        var stats = pool.GetStatistics();

        // 两者必须来自同一次快照：分两次读会让计数与数组长度背离。
        Assert.Equal(stats.Endpoints.Count, stats.EndpointCount);
        Assert.Equal(2, stats.EndpointCount);
    }

    // ===============================================================
    // Bug 11：容量驱逐声称 LRU 实际按任意序
    // ===============================================================

    [Fact]
    public void EnsureCapacity_EvictsLeastRecentlyUsedIdleEndpoint()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 2,
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        // A 先、B 后，再复用一次 A：B 成为最久未使用的空闲节点
        using (var a = pool.Lease(new Uri("http://127.0.0.1:9201/x")))
        {
        }

        Thread.Sleep(50);

        using (var b = pool.Lease(new Uri("http://127.0.0.1:9202/x")))
        {
        }

        Thread.Sleep(50);

        using (var aAgain = pool.Lease(new Uri("http://127.0.0.1:9201/x")))
        {
        }

        // 第三个 origin 触发容量检查：应驱逐 B 而不是任意一个
        using var c = pool.Lease(new Uri("http://127.0.0.1:9203/x"));

        var stats = pool.GetStatistics();
        Assert.Equal(2, stats.EndpointCount);
        Assert.Contains(stats.Endpoints, e => e.Origin.EndsWith(":9201"));
        Assert.Contains(stats.Endpoints, e => e.Origin.EndsWith(":9203"));
        Assert.DoesNotContain(stats.Endpoints, e => e.Origin.EndsWith(":9202"));
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
    public void KeepAlivePing_InvalidValues_FailAtConstruction()
    {
        // 0 不是「不 ping」而是「立刻 ping」：底层会照做并把连接打爆，必须在构造期拦住。
        var zeroDelay = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { KeepAlivePingDelay = TimeSpan.Zero }));
        Assert.Equal(AxiosException.ErrBadOptionValue, zeroDelay.Code);

        var zeroTimeout = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { KeepAlivePingTimeout = TimeSpan.Zero }));
        Assert.Equal(AxiosException.ErrBadOptionValue, zeroTimeout.Code);

        // 关掉 keep-alive ping 的合法写法只有 InfiniteTimeSpan 一种
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            KeepAlivePingDelay = Timeout.InfiniteTimeSpan,
            EvictionInterval = TimeSpan.Zero
        });
        Assert.Equal(Timeout.InfiniteTimeSpan, pool.Options.KeepAlivePingDelay);
    }

    [Fact]
    public void KeepAlivePing_InvalidPolicy_FailsAtConstruction()
    {
        // 未定义的枚举值底层会当成「不 ping」：静默忽略等于让调用方以为配上了。
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { KeepAlivePingPolicy = (HttpKeepAlivePingPolicy)99 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task KeepAlivePing_IsAppliedToDefaultHandler()
    {
        SocketsHttpHandler? captured = null;
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(45),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            HandlerFactory = ctx => captured = ctx.CreateDefaultHandler()
        });

        await client.GetAsync(server.Url + "keepalive");

        // 池选项最终要落到真正发请求的 Handler 上：漏一个赋值，h2 长连接就还是会被中间设备静默回收。
        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(45), captured!.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), captured.KeepAlivePingTimeout);
        Assert.Equal(HttpKeepAlivePingPolicy.Always, captured.KeepAlivePingPolicy);
    }

    [Fact]
    public void KeepAlivePing_Defaults_MatchSocketsHttpHandler()
    {
        // 默认值本身是契约：ping 默认关闭（与 SocketsHttpHandler 一致，不悄悄改线上行为），
        // 打开后用 20s 等 ACK、只在有在途请求时 ping。
        var options = new ConnectionPoolOptions();

        Assert.Equal(Timeout.InfiniteTimeSpan, options.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(20), options.KeepAlivePingTimeout);
        Assert.Equal(HttpKeepAlivePingPolicy.WithActiveRequests, options.KeepAlivePingPolicy);
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
    public void PooledConnectionLifetime_IsJitteredWithinTenPercent()
    {
        var configured = TimeSpan.FromMinutes(10);
        var distinct = new HashSet<long>();

        for (var i = 0; i < 200; i++)
        {
            var jittered = DefaultHandlerFactory.JitterLifetime(configured);
            var ratio = jittered.Ticks / (double)configured.Ticks;

            Assert.InRange(ratio, 0.9d, 1.1d);
            distinct.Add(jittered.Ticks);
        }

        // ±10% 是随机区间：两百次都取到同一个值说明抖动根本没接上
        Assert.True(distinct.Count > 1, "PooledConnectionLifetime 没有被抖动");
    }

    [Fact]
    public void PooledConnectionLifetime_ZeroAndInfinite_AreNotJittered()
    {
        Assert.Equal(TimeSpan.Zero, DefaultHandlerFactory.JitterLifetime(TimeSpan.Zero));
        Assert.Equal(Timeout.InfiniteTimeSpan, DefaultHandlerFactory.JitterLifetime(Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void DefaultHandler_AppliesJitteredLifetime_ButOptionsKeepConfiguredValue()
    {
        var configured = TimeSpan.FromMinutes(10);
        var options = new ConnectionPoolOptions { PooledConnectionLifetime = configured };

        using var handler = DefaultHandlerFactory.Create(options);

        Assert.InRange(handler.PooledConnectionLifetime, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        // 抖动只落在 Handler 上：选项保持原值，命名池的 SameOptions 比较不受影响
        Assert.Equal(configured, options.PooledConnectionLifetime);
    }

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

        // 闲置窗口不能压到「刚用完就要过期」的量级：本用例要断言「刚发过请求的节点还在」
        // （104→105 与 116→118 两处），而断言与请求之间只要有 > 闲置窗口 的调度停顿
        // （GC、线程池排队、并行用例抢 CPU），后台清扫就会把那个节点合法淘汰掉，断言随即假红。
        // 2 秒给断言留 10 倍余量，清扫间隔仍取 80ms，保证「后台驱逐」这条路本身被真正走到。
        var poolOptions = new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromSeconds(2),
            EvictionInterval = TimeSpan.FromMilliseconds(80)
        };
        using var pool = new HttpConnectionPool(poolOptions);
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);

        var deadline = DateTime.UtcNow.AddSeconds(10);
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

    // ---------------------------------------------------------------
    // Cookie：默认关闭；WithCredentials 打开并带上默认系统凭据
    // ---------------------------------------------------------------

    [Fact]
    public async Task UseCookies_DisabledByDefault_DoesNotSendStoredCookies()
    {
        await using var authServer = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/" }));
        await using var apiServer = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        await client.GetAsync(authServer.Url + "login");
        await client.GetAsync(apiServer.Url + "data");

        Assert.False(apiServer.Received[0].Headers.ContainsKey("cookie"));
    }

    [Fact]
    public void WithCredentials_EnablesCookiesAndDefaultCredentialsOnPool()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        Assert.True(client.Pool!.Options.UseCookies);
        Assert.NotNull(client.Pool.Options.Credentials);
    }

    [Fact]
    public void PoolOptions_Clone_CopiesUseCookies()
    {
        var options = new ConnectionPoolOptions { UseCookies = true };

        Assert.True(options.Clone().UseCookies);
    }

    // ------------------------------------------------------------------
    // 池选项：Timeout.InfiniteTimeSpan 表示「不限」
    // ------------------------------------------------------------------

    [Fact]
    public async Task PoolOptions_InfiniteTimeSpan_IsAccepted_AndRequestSucceeds()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            IdleHandlerLifetime = Timeout.InfiniteTimeSpan,
            ConnectTimeout = Timeout.InfiniteTimeSpan
        });

        // 之前这三项用 -1ms（InfiniteTimeSpan）会被判成「负数」直接抛错，
        // 而报错文案本身写的就是「不限请用 Timeout.InfiniteTimeSpan」——照文档改只会再炸一次。
        using var client = new AxiosClient(pool: pool);
        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        Assert.Equal(Timeout.InfiniteTimeSpan, pool.Options.PooledConnectionLifetime);
    }

    [Fact]
    public void PoolOptions_NegativeButNotInfinite_StillThrows()
    {
        var ex = Assert.Throws<AxiosException>(() => new HttpConnectionPool(new ConnectionPoolOptions
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(-5)
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // Cookie 容器按域共享，而不是按 origin 切开

    [Fact]
    public async Task Cookies_SetOnOneOrigin_AreSentToAnotherOrigin()
    {
        await using var authServer = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/" }));
        await using var apiServer = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions { UseCookies = true });

        await client.GetAsync(authServer.Url + "login");
        await client.GetAsync(apiServer.Url + "data");

        Assert.Contains("session=abc", apiServer.Received[0].Headers["cookie"]);
    }

    // 池的 origin 节点数上限

    [Fact]
    public async Task Pool_EvictsIdleOrigin_WhenCapacityIsReached()
    {
        await using var first = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(first.Url + "a");
        await client.GetAsync(second.Url + "b");

        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task Pool_RejectsNewOrigin_WhenEveryNodeIsBusy()
    {
        await using var first = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(1500);
            return TestJson.Response(new { ok = true });
        });
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        var slow = client.GetAsync(first.Url + "slow");
        await WaitUntilAsync(() => first.Received.Count > 0);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(second.Url + "b"));
        Assert.Equal(AxiosException.ErrPoolExhausted, ex.Code);

        Assert.Equal(200, (await slow).Status);
    }

    // ------------------------------------------------------------------
    // L13：租用与池关闭的竞态不留下不可回收的节点
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lease_RacingWithDispose_DoesNotLeakEndpoints()
    {
        var uri = new Uri("http://127.0.0.1:1/x");

        for (var i = 0; i < 48; i++)
        {
            using var pool = new HttpConnectionPool(new ConnectionPoolOptions
            {
                IdleHandlerLifetime = TimeSpan.Zero,
                EvictionInterval = TimeSpan.Zero
            });

            using var start = new ManualResetEventSlim(false);
            var leasing = Task.Run(() =>
            {
                start.Wait();
                for (var j = 0; j < 32; j++)
                {
                    try
                    {
                        using var lease = pool.Lease(uri);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });

            start.Set();
            pool.Dispose();
            await leasing;

            // 关闭后新建的节点若不带 DisposeWhenIdle，归还时既不进字典也不释放
            Assert.Equal(0, pool.GetStatistics().EndpointCount);
        }
    }

    [Fact]
    public async Task PoolClearedDuringRequest_ThrowsAxiosException()
    {
        var gate = new TaskCompletionSource();
        await using var server = TestHttpServer.Start(async req =>
        {
            if (req.Path.EndsWith("hold", StringComparison.Ordinal))
            {
                await gate.Task;
            }

            return TestJson.Response(new { ok = true });
        });

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30),
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url + "warm");
        var pending = client.GetAsync(server.Url + "hold");
        await Task.Delay(120);

        pool.Clear();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => pending);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);

        gate.SetResult();
    }

    [Fact]
    public async Task RequestAfterPoolDispose_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();
        using var client = new AxiosClient(pool: pool);

        pool.Dispose();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
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

    // ---------------------------------------------------------------
    // 驱逐路径：锁内判定、锁外释放（P1：不得持锁关闭 Handler）
    // ---------------------------------------------------------------

    [Fact]
    public async Task Pool_EvictIdle_ReleasesHandlerOutsideLock_PoolStaysResponsive()
    {
        var disposeEntered = new ManualResetEventSlim(false);
        var releaseDispose = new ManualResetEventSlim(false);

        // 工厂第一次调用对应 9000（唯一会被驱逐的 origin）：让它拿到受控实例，
        // 之后的新 origin 各拿独立实例（池约定：一个 Handler 实例不能跨 origin 共用）。
        var first = new BlockingDisposeHandler_OwnershipAndPool(disposeEntered, releaseDispose);
        var created = 0;

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            HandlerFactory = _ => Interlocked.Increment(ref created) == 1
                ? first
                : new BlockingDisposeHandler_OwnershipAndPool(disposeEntered, releaseDispose),
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        using var lease = pool.Lease(new Uri("http://127.0.0.1:9000/x"));
        lease.Dispose();

        var evictTask = Task.Run(() => pool.EvictIdleEndpoints());
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)), "驱逐没有进入 Handler 释放。");

        // Handler 正在关闭（这段持着 endpoint.SyncRoot）：
        // 池上的其它操作必须不被它拖住——统计要遍历各节点的锁，新 origin 租用要走 EnsureCapacity。
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
        using var other = pool.Lease(new Uri("http://127.0.0.1:9001/y"));
        Assert.Equal(1, pool.GetStatistics().EndpointCount);

        releaseDispose.Set();
        Assert.Equal(1, await evictTask);
        Assert.True(first.Disposed, "被驱逐节点的 Handler 未被释放。");

        other.Dispose();
        Assert.All(pool.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));
    }

    [Fact]
    public async Task Pool_EvictIdle_ConcurrentWithLeasing_StaysConsistent()
    {
        using var pool = NewPool();
        var stop = false;

        var leaser = Task.Run(() =>
        {
            var i = 0;
            while (!Volatile.Read(ref stop))
            {
                using var lease = pool.Lease(new Uri($"http://127.0.0.1:{9200 + (i++ % 8)}/x"));
            }
        });

        // 驱逐与租用并发：锁内只做判定+摘除+置位，释放全部移到锁外，
        // 两侧不能因为持锁释放 Handler 而互相排队（更不能死锁）。
        for (var round = 0; round < 50; round++)
        {
            pool.EvictIdleEndpoints();
            await Task.Delay(1);
        }

        Volatile.Write(ref stop, true);
        await leaser.WaitAsync(TimeSpan.FromSeconds(10));

        // 租约全部归还后，池必须能回到干净状态：
        // 8 个 origin 的节点都空闲，一次驱逐即应清零（不再残留、也不再抛异常）。
        Assert.All(pool.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));
        Assert.InRange(pool.EvictIdleEndpoints(), 0, 8);
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
    }

    // ---------------------------------------------------------------
    // 并发压测：驱逐 / 清空 / 关闭 vs 并发租用
    //
    // 三条路径都会「摘字典 + 置关闭标记」，而租用路径靠 TryAcquire 的返回值重试：
    // 这里把两侧真正叠在一起跑，锁的是不变量（不抛、不漏、不重复释放、计数不为负），
    // 后续把租约计数改成 CAS 状态字时必须仍然满足。
    // ---------------------------------------------------------------

    [Fact]
    public async Task Pool_EvictIdle_ConcurrentWithLeasing_ReleasesEveryHandlerExactlyOnce()
    {
        var ledger = new HandlerLedger();
        using var pool = NewStressPool(ledger);

        var stop = 0;
        var leasers = StartLeasers(pool, firstPort: 9300, originCount: 8, workers: 4, () => Volatile.Read(ref stop) == 0);

        for (var round = 0; round < 200; round++)
        {
            pool.EvictIdleEndpoints();
            await Task.Delay(1);
        }

        Volatile.Write(ref stop, 1);
        await Task.WhenAll(leasers).WaitAsync(TimeSpan.FromSeconds(20));

        // 全部归还后：没有残留租约，最后一次性驱逐能把池清空
        Assert.All(pool.GetStatistics().Endpoints, e => Assert.Equal(0, e.ActiveLeases));
        var remaining = pool.GetStatistics().EndpointCount;
        Assert.Equal(remaining, pool.EvictIdleEndpoints());
        Assert.Equal(0, pool.GetStatistics().EndpointCount);

        // 每个被驱逐的节点都必须释放自己的 Handler，且只释放一次
        Assert.True(ledger.Created > 0, "压测期间没有创建任何 Handler，租用路径未被覆盖。");
        Assert.Equal(ledger.Created, ledger.Disposed);
    }

    [Fact]
    public async Task Pool_Clear_ConcurrentWithLeasing_ReleasesEveryHandlerExactlyOnce()
    {
        var ledger = new HandlerLedger();
        using var pool = NewStressPool(ledger);

        var stop = 0;
        var leasers = StartLeasers(pool, firstPort: 9310, originCount: 8, workers: 4, () => Volatile.Read(ref stop) == 0);

        // Clear 是强制版：可能直接 Dispose 掉别人正持有的节点，归还租约时必须安全
        for (var round = 0; round < 200; round++)
        {
            pool.Clear();
            await Task.Delay(1);
        }

        Volatile.Write(ref stop, 1);
        await Task.WhenAll(leasers).WaitAsync(TimeSpan.FromSeconds(20));

        pool.Clear();

        Assert.Equal(0, pool.GetStatistics().EndpointCount);
        Assert.True(ledger.Created > 0, "压测期间没有创建任何 Handler，租用路径未被覆盖。");
        Assert.Equal(ledger.Created, ledger.Disposed);
    }

    [Fact]
    public async Task Pool_Dispose_ConcurrentWithLeasing_InFlightLeasesFinishAndPoolDrains()
    {
        var ledger = new HandlerLedger();
        var pool = NewStressPool(ledger);

        var stop = 0;
        var rejected = 0;
        var leasers = StartLeasers(
            pool,
            firstPort: 9320,
            originCount: 8,
            workers: 4,
            () => Volatile.Read(ref stop) == 0,
            onRejected: () => Interlocked.Increment(ref rejected));

        // 先确保租用已经跑起来，再持有确定的在途租约去触发优雅关闭
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ledger.Created == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }

        Assert.True(ledger.Created > 0, "压测期间没有创建任何 Handler，租用路径未被覆盖。");

        var inFlight = pool.Lease(new Uri("http://127.0.0.1:9330/x"));
        pool.Dispose();

        // 优雅关闭不能打断在途租约：归还必须安全，且节点在最后一条租约归还时自行摘除
        inFlight.Dispose();
        inFlight.Dispose();

        // 关闭后再放租用线程跑一会儿：它们必须全部拿到 ObjectDisposedException 并收手，
        // 而不是卡在重试循环里或把异常留给 WhenAll 之外的调用方。
        await Task.Delay(50);
        Volatile.Write(ref stop, 1);
        await Task.WhenAll(leasers).WaitAsync(TimeSpan.FromSeconds(20));

        // 关闭后的新租用一律被拒
        Assert.Throws<ObjectDisposedException>(() => pool.Lease(new Uri("http://127.0.0.1:9330/x")));
        Assert.Equal(leasers.Length, rejected);

        // 所有租约归还后池必须排空，Handler 全部释放（包含「关闭后才插入」的节点）
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
        Assert.Equal(ledger.Created, ledger.Disposed);
    }

    [Fact]
    public async Task Pool_GetStatistics_StaysSelfConsistent_WhileEvictingAndLeasing()
    {
        var ledger = new HandlerLedger();
        using var pool = NewStressPool(ledger);

        var stop = 0;
        var leasers = StartLeasers(pool, firstPort: 9340, originCount: 8, workers: 4, () => Volatile.Read(ref stop) == 0);

        var evictor = Task.Run(async () =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                pool.EvictIdleEndpoints();
                await Task.Delay(1);
            }
        });

        for (var round = 0; round < 500; round++)
        {
            var stats = pool.GetStatistics();

            // EndpointCount 与 Endpoints 必须来自同一次快照，租约计数不允许为负
            Assert.Equal(stats.Endpoints.Count, stats.EndpointCount);
            Assert.All(stats.Endpoints, e => Assert.True(e.ActiveLeases >= 0));
        }

        Volatile.Write(ref stop, 1);
        await Task.WhenAll(leasers).WaitAsync(TimeSpan.FromSeconds(20));
        await evictor.WaitAsync(TimeSpan.FromSeconds(20));
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

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // 表单 / multipart / params 展平的环检测。
    private sealed class Node
    {
        public string? Name { get; set; }
        public List<Node> Children { get; } = new();
    }


    private sealed class BlockingDisposeHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }

    // IReadOnlyDictionary 按键值对展平。


    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    private sealed class Node_AuditRegression
    {
        public Node_AuditRegression? Self { get; set; }
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

    private static HttpConnectionPool NewPool(int maxEndpointCount = 1024)
        => new(new ConnectionPoolOptions
        {
            MaxEndpointCount = maxEndpointCount,
            // 全程关闭后台驱逐线程，用例自己决定何时驱逐
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

    /// <summary>Dispose 时阻塞到手动放行的 Handler，用于验证「释放不在锁内执行」。</summary>
    private sealed class BlockingDisposeHandler_OwnershipAndPool : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler_OwnershipAndPool(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }

    private static HttpConnectionPool NewStressPool(HandlerLedger ledger)
        => new(new ConnectionPoolOptions
        {
            HandlerFactory = _ => ledger.Create(),
            // 全程关闭后台驱逐：用例自己控制驱逐节奏，避免时间因素带来的不确定性
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

    /// <summary>
    /// 起若干个持续「租用→归还」的线程；驱逐/清空/关闭可能把节点从脚下摘掉，
    /// 租用必须靠重试透明恢复，除了关闭场景的 <see cref="ObjectDisposedException"/> 外不得抛异常。
    /// </summary>
    private static Task[] StartLeasers(
        HttpConnectionPool pool,
        int firstPort,
        int originCount,
        int workers,
        Func<bool> shouldContinue,
        Action? onRejected = null)
        => Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            var i = worker;
            while (shouldContinue())
            {
                PoolLease lease;
                try
                {
                    lease = pool.Lease(new Uri($"http://127.0.0.1:{firstPort + (i++ % originCount)}/x"));
                }
                catch (ObjectDisposedException)
                {
                    onRejected?.Invoke();
                    return;
                }

                // 租约持有期间池可能被驱逐 / 清空 / 关闭：归还必须幂等且不抛（抛了任务就失败）
                lease.Dispose();
            }
        })).ToArray();

    // ------------------------------------------------------------------
    // 命名共享池（进程级表：key 由调用方给，不做配置指纹）
    // ------------------------------------------------------------------

    /// <summary>用例独占的池名：表是进程级的，用随机 key 避免与并行用例互相干扰。</summary>
    private static string UniquePoolKey() => "test-pool-" + Guid.NewGuid().ToString("N");

    // 同一 key 取两次是同一条池（命名共享的本意）。
    [Fact]
    public void NamedPool_GetOrCreate_SameKey_ReturnsSameInstance()
    {
        var key = UniquePoolKey();

        var first = HttpConnectionPool.GetOrCreate(key);
        var second = HttpConnectionPool.GetOrCreate(key);

        try
        {
            Assert.Same(first, second);
            Assert.True(HttpConnectionPool.TryGet(key, out var looked));
            Assert.Same(first, looked);
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // 只取池（不传 options）是幂等的：重复传一份等价配置同样放行。
    [Fact]
    public void NamedPool_GetOrCreate_EquivalentOptions_IsIdempotent()
    {
        var key = UniquePoolKey();
        var options = new ConnectionPoolOptions { MaxConnectionsPerServer = 8, EvictionInterval = TimeSpan.Zero };

        var first = HttpConnectionPool.GetOrCreate(key, options);
        var second = HttpConnectionPool.GetOrCreate(key, options);

        try
        {
            Assert.Same(first, second);
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // 同一 key 上传了不同配置：明确报错，不静默忽略第二次的配置。
    [Fact]
    public void NamedPool_GetOrCreate_ConflictingOptions_ThrowsAxiosException()
    {
        var key = UniquePoolKey();
        var first = HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { MaxConnectionsPerServer = 8 });

        try
        {
            var ex = Assert.Throws<AxiosException>(() =>
                HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { MaxConnectionsPerServer = 16 }));

            Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
            Assert.Contains(key, ex.Message, StringComparison.Ordinal);
            Assert.Same(first, HttpConnectionPool.GetOrCreate(key));   // 报错后原池仍在
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // 委托 / 代理这类引用成员没有可靠的结构等价：两份「长得一样」的配置也不共用一条池。
    [Fact]
    public void NamedPool_GetOrCreate_DistinctHandlerFactoryInstances_Conflict()
    {
        var key = UniquePoolKey();
        HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { HandlerFactory = _ => new HttpClientHandler() });

        try
        {
            Assert.Throws<AxiosException>(() =>
                HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { HandlerFactory = _ => new HttpClientHandler() }));
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // 新增的标量选项同样参与「同名池配置必须一致」的判定：漏一个字段会让两份不同配置静默共用一条池。
    [Fact]
    public void NamedPool_GetOrCreate_ConflictingKeepAlivePing_Conflict()
    {
        var key = UniquePoolKey();
        HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });

        try
        {
            var ex = Assert.Throws<AxiosException>(() => HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions
            {
                EvictionInterval = TimeSpan.Zero,
                KeepAlivePingDelay = TimeSpan.FromSeconds(45)
            }));

            Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // Release 摘除并优雅释放：此后取不到，旧引用上的租约也报 ObjectDisposedException。
    [Fact]
    public void NamedPool_Release_RemovesAndDisposesPool()
    {
        var key = UniquePoolKey();
        var pool = HttpConnectionPool.GetOrCreate(key);

        Assert.True(HttpConnectionPool.Release(key));
        Assert.False(HttpConnectionPool.Release(key));            // 重复释放：不命中
        Assert.False(HttpConnectionPool.TryGet(key, out _));
        Assert.Throws<ObjectDisposedException>(() => pool.Lease(new Uri("http://127.0.0.1:9500/x")));
    }

    // 命名池不归客户端所有：客户端 Dispose 之后池照样能用（与显式传的共享池同一语义）。
    [Fact]
    public void NamedPool_IsNotOwnedByClient()
    {
        var key = UniquePoolKey();
        var pool = HttpConnectionPool.GetOrCreate(key);

        try
        {
            var client = new AxiosClient(pool: pool);
            client.Dispose();

            using (pool.Lease(new Uri("http://127.0.0.1:9500/x")))
            {
            }

            Assert.Equal(1, pool.GetStatistics().EndpointCount);   // 客户端释放后池照样能建节点
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    // key 为空白：直接拒绝（空 key 会让互不相干的调用方意外共享同一条池）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NamedPool_BlankKey_IsRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => HttpConnectionPool.GetOrCreate(key));
        Assert.Throws<ArgumentException>(() => HttpConnectionPool.Release(key));
    }

    // 与 Release 抢同一个 key：登记与摘除并发时不许抛（落选后不能假定表里还有这一项）。
    [Fact]
    public void NamedPool_GetOrCreate_RacingWithRelease_DoesNotThrow()
    {
        var key = UniquePoolKey();
        var failures = new ConcurrentBag<Exception>();

        Parallel.For(0, 32, i =>
        {
            try
            {
                if (i % 4 == 0)
                {
                    HttpConnectionPool.Release(key);
                }
                else
                {
                    HttpConnectionPool.GetOrCreate(key);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        HttpConnectionPool.Release(key);
        Assert.Empty(failures);
    }

    // 命名池被 Release 之后，客户端发请求仍然走统一错误模型：ERR_CANCELED，不是裸 ObjectDisposedException。
    [Fact]
    public async Task NamedPool_AfterRelease_ClientRequest_ReportsCanceled()
    {
        var key = UniquePoolKey();
        var pool = HttpConnectionPool.GetOrCreate(key);
        using var client = new AxiosClient(pool: pool);

        HttpConnectionPool.Release(key);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:9500/x"));
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    // key 为 null：ArgumentNullException（与库里其余入口的入参校验同一口径）。
    [Fact]
    public void NamedPool_NullKey_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => HttpConnectionPool.GetOrCreate(null!));
        Assert.Throws<ArgumentNullException>(() => HttpConnectionPool.Release(null!));
    }

    // 并发抢占同一个新 key：只能有一条池活下来（落选的那条必须被释放，否则后台定时器会永久持有它）。
    [Fact]
    public void NamedPool_ConcurrentGetOrCreate_YieldsOnePool()
    {
        var key = UniquePoolKey();
        var results = new HttpConnectionPool[16];

        try
        {
            Parallel.For(0, results.Length, i => results[i] = HttpConnectionPool.GetOrCreate(key));

            Assert.All(results, pool => Assert.Same(results[0], pool));
            Assert.Same(results[0], HttpConnectionPool.GetOrCreate(key));
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    /// <summary>记录 Handler 创建/释放次数：用于断言驱逐与关闭「不漏释放、不重复释放」。</summary>
    private sealed class HandlerLedger
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public HttpMessageHandler Create()
        {
            Interlocked.Increment(ref _created);
            return new LedgerHandler(this);
        }

        private sealed class LedgerHandler : HttpMessageHandler
        {
            private readonly HandlerLedger _owner;
            private int _disposed;

            public LedgerHandler(HandlerLedger owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                base.Dispose(disposing);
            }
        }
    }
}