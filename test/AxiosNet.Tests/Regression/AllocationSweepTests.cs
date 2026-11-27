
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

// 本文件的用例测的就是「同步路径的字节分配」：改成 await 会把 async 状态机与 Task 算进被测开销，
// 因此刻意用 GetAwaiter().GetResult() 同步消费（底层都是同步完成的 ValueTask）。
#pragma warning disable xUnit1031

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 分配审计：空闲清扫不复制字典、闸门快路径不进状态机、同步读零分配。
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class AllocationSweepTests
{
    [Fact]
    public void Pool_IdleSweep_DoesNotSnapshotTheWholeDictionary()
    {
        const int Endpoints = 24;

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            // 关闭后台定时器，本用例自己驱动清扫。
            IdleHandlerLifetime = TimeSpan.FromMinutes(10),
            EvictionInterval = TimeSpan.Zero,
            MaxEndpointCount = 64
        });

        // 全部持租约 = 全部不可驱逐；扫描走完整条链路。
        var leases = new List<PoolLease>(Endpoints);
        try
        {
            for (var i = 0; i < Endpoints; i++)
            {
                leases.Add(pool.Lease(new Uri($"http://127.0.0.1:{60000 + i}/")));
            }

            // 空闲清扫的单次分配（护栏 256 B）。
            var floor = MinimumBytes(200, () => pool.EvictIdleEndpoints());

            Assert.True(
                floor < 256,
                $"空闲清扫最小 {floor} B/次（{Endpoints} 个节点），超出护栏 256 B——"
                + "疑似又回到了「先 ToArray 整张字典再遍历」的路径：清扫不需要快照，"
                + "并发字典的枚举器本身就是结构体。");
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void Pool_IdleSweep_StillEvictsIdleEndpointsAndKeepsBusyOnes()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.Zero, // 任何空闲节点立即可驱逐
            EvictionInterval = TimeSpan.Zero,
            MaxEndpointCount = 64
        });

        var busy = pool.Lease(new Uri("http://127.0.0.1:61001/"));
        using var idle = pool.Lease(new Uri("http://127.0.0.1:61002/"));

        Assert.Equal(2, pool.GetStatistics().EndpointCount);

        // 归还租约 = 变成空闲；在途的那个必须留下。
        idle.Dispose();
        var evicted = pool.EvictIdleEndpoints();

        try
        {
            Assert.Equal(1, evicted);

            var statistics = pool.GetStatistics();
            Assert.Equal(1, statistics.EndpointCount);
            Assert.Equal(1, statistics.Endpoints[0].ActiveLeases);
        }
        finally
        {
            busy.Dispose();
        }
    }

    [Fact]
    public async Task Gate_FastPath_DoesNotPayForAsyncMachinery()
    {
        var gate = new ConcurrencyGate(8);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:59999/"));
        var config = new AxiosConfig();

        // 同步取 / 还；用 GetAwaiter().GetResult() 同步等待。
        void AcquireOnce()
        {
            gate.AcquireAsync(config, request, CancellationToken.None, TimeSpan.Zero, budgetRemaining: null, attempt: 1)
                .GetAwaiter()
                .GetResult()
                .Dispose();
        }

        await Task.Yield(); // 让测试方法自己不占测量窗口

        // 槽位空：走「Wait(0) 成功」快路径（护栏 64 B）。
        var floor = MinimumBytes(500, AcquireOnce);

        Assert.True(
            floor < 64,
            $"并发槽位快路径最小 {floor} B/次，超出护栏 64 B——"
            + "同步就能拿到槽位的路径不该建 async 状态机，应拆成「同步快路径 + 排队慢路径」。");
    }

    [Fact]
    public void Download_SynchronousChunkRead_DoesNotAllocate()
    {
        var payload = new byte[64 * 1024];
        var chunk = new byte[8 * 1024];
        var inner = new NeverDisposingMemoryStream(payload);
        var config = new AxiosConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:59999/"));

        // 进度 / 限速包装：无回调无限速时每次读只做读 + 记账 + 判上限（护栏每 64 KB 合计 64 B）。
        using var stream = new ProgressReadStream(inner, payload.Length, null, null, null, config, request);

        void ReadWholeBody()
        {
            inner.Position = 0;
            for (var i = 0; i < 8; i++)
            {
                stream.ReadAsync(chunk.AsMemory(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var floor = MinimumBytes(200, ReadWholeBody);

        Assert.True(
            floor < 64,
            $"进度包装流读 64 KB（8 块）最小 {floor} B，超出护栏 64 B——内层同步完成时"
            + "整条读链路不该有任何分配。");
    }

    [Fact]
    public void StreamWindow_SynchronousChunkRead_DoesNotAllocate()
    {
        var payload = new byte[64 * 1024];
        var chunk = new byte[8 * 1024];
        var inner = new NeverDisposingMemoryStream(payload);
        var config = new AxiosConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:59999/"));

        // 超时窗口包装（护栏每 64 KB 合计 64 B）。
        using var stream = new TimeoutStream(inner, TimeSpan.FromSeconds(30), CancellationToken.None, config, request);

        void ReadWholeBody()
        {
            inner.Position = 0;
            for (var i = 0; i < 8; i++)
            {
                stream.ReadAsync(chunk.AsMemory(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var floor = MinimumBytes(200, ReadWholeBody);

        Assert.True(
            floor < 64,
            $"超时窗口流读 64 KB（8 块）最小 {floor} B，超出护栏 64 B——内层同步完成时"
            + "整条读链路不该有任何分配。");
    }

    /// <summary>多次取最小值：被测路径同步，噪声只会往上加。</summary>
    /// <remarks>计数器用当前线程而非进程总量。</remarks>
    private static long MinimumBytes(int iterations, Action action, int windows = 3)
    {
        action(); // 预热：JIT、静态缓存、首次反射
        var best = long.MaxValue;
        for (var window = 0; window < windows; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                action();
            }

            best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / iterations);
        }

        return best;
    }

    /// <summary>包装流会释放内层流，本用例要反复复用同一份内存，所以忽略释放。</summary>
    private sealed class NeverDisposingMemoryStream : MemoryStream
    {
        public NeverDisposingMemoryStream(byte[] buffer)
            : base(buffer)
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
