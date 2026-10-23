using System.Net;
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
        var first = new BlockingDisposeHandler(disposeEntered, releaseDispose);
        var created = 0;

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            HandlerFactory = _ => Interlocked.Increment(ref created) == 1
                ? first
                : new BlockingDisposeHandler(disposeEntered, releaseDispose),
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        using var lease = pool.Lease(new Uri("http://127.0.0.1:9000/x"));
        lease.Dispose();

        var evictTask = Task.Run(() => pool.EvictIdleEndpoints());
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)), "驱逐没有进入 Handler 释放。");

        // Handler 正在关闭（修复前这段持着 endpoint.SyncRoot）：
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

    /// <summary>Dispose 时阻塞到手动放行的 Handler，用于验证「释放不在锁内执行」。</summary>
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
