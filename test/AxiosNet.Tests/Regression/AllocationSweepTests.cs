
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

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
            // 关闭后台定时器：本用例自己驱动清扫，避免周期回调污染进程级计数。
            IdleHandlerLifetime = TimeSpan.FromMinutes(10),
            EvictionInterval = TimeSpan.Zero,
            MaxEndpointCount = 64
        });

        // 全部持租约 = 全部不可驱逐：扫描会走完整条链路，但一个都不会被移走。
        // 这么做是为了让「扫描」本身的成本暴露出来（能驱逐时反而提前退出，测不出快照开销）。
        var leases = new List<PoolLease>(Endpoints);
        try
        {
            for (var i = 0; i < Endpoints; i++)
            {
                leases.Add(pool.Lease(new Uri($"http://127.0.0.1:{60000 + i}/")));
            }

            // 修复前实测 810 B/次（24 个节点 × KeyValuePair 快照数组）：这些数组纯粹是
            // 「为了排序 / 遍历而复制」，节点一个也没被驱逐时更是白付。护栏 256 B。
            var average = AverageBytes(200, () => pool.EvictIdleEndpoints());

            Assert.True(
                average < 256,
                $"空闲清扫平均 {average} B/次（{Endpoints} 个节点），超出护栏 256 B——"
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

        // 同步取 / 还：被测代码同步完成，测量本身就不能再引入异步机制——
        // 写成 async 本地函数的话，每个 Task 也是 70 B 上下，量到的是测试自己。
        // 用 GetAwaiter().GetResult() 走同步等待（ValueTask 已成功，不会阻塞）。
        void AcquireOnce()
        {
            gate.AcquireAsync(config, request, CancellationToken.None, TimeSpan.Zero, budgetRemaining: null, attempt: 1)
                .GetAwaiter()
                .GetResult()
                .Dispose();
        }

        await Task.Yield(); // 让测试方法自己不占测量窗口

        // 槽位总是空的：走的是「Wait(0) 成功」的快路径。修复前实测 247 B/次
        //（SlotLease 对象 24 B + 两层 async 状态机），护栏 64 B。
        var average = AverageBytes(500, AcquireOnce);

        Assert.True(
            average < 64,
            $"并发槽位快路径平均 {average} B/次，超出护栏 64 B——"
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

        // 进度 / 限速包装：没有回调也没有限速时，每次读只是「读 + 记账 + 判上限」，
        // 修复前实测 212 B/chunk（两层 async 状态机），护栏每 64 KB 合计 64 B。
        using var stream = new ProgressReadStream(inner, payload.Length, null, null, null, config, request);

        void ReadWholeBody()
        {
            inner.Position = 0;
            for (var i = 0; i < 8; i++)
            {
                stream.ReadAsync(chunk.AsMemory(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var average = AverageBytes(200, ReadWholeBody);

        Assert.True(
            average < 64,
            $"进度包装流读 64 KB（8 块）平均 {average} B，超出护栏 64 B——内层同步完成时"
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

        // 超时窗口包装：修复前实测 177 B/chunk，护栏每 64 KB 合计 64 B。
        using var stream = new TimeoutStream(inner, TimeSpan.FromSeconds(30), CancellationToken.None, config, request);

        void ReadWholeBody()
        {
            inner.Position = 0;
            for (var i = 0; i < 8; i++)
            {
                stream.ReadAsync(chunk.AsMemory(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var average = AverageBytes(200, ReadWholeBody);

        Assert.True(
            average < 64,
            $"超时窗口流读 64 KB（8 块）平均 {average} B，超出护栏 64 B——内层同步完成时"
            + "整条读链路不该有任何分配。");
    }

    private static long AverageBytes(int iterations, Action action)
    {
        action(); // 预热：JIT、静态缓存、首次反射
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return (GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
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