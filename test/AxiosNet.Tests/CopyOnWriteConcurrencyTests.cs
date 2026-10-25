using System.Collections.Concurrent;
using Xunit;

namespace AxiosNet.Tests;

/// <summary>
/// 写时复制的<b>并发契约</b>：<see cref="AxiosHeaders"/> 与 <c>SynchronizedList</c> 共用同一套 Owners 模型，
/// 这里锁住它最容易写错的一条 —— 「判定独占」与「真正写入」必须在同一个临界区里。
/// <list type="bullet">
/// <item>丢更新：写方判定独占后、写入前若被克隆/分离插队，旧实现会把这次写入写进一个再无人读取的存储；</item>
/// <item>零复制：共享期间「清空一个空容器」「删除一个不存在的键」不该付整份复制的代价。</item>
/// </list>
/// </summary>
public sealed class CopyOnWriteConcurrencyTests
{
    // ------------------------------------------------------------------
    // 丢更新
    // ------------------------------------------------------------------

    [Fact]
    public void Headers_ConcurrentCloneAndSet_NeverLosesUpdate()
    {
        // 头数放大到 64：分离要复制整份字典，把「判定独占 → 写入」之间的窗口拉长，更容易撞上。
        for (var round = 0; round < 20; round++)
        {
            var source = NewHeaders(64);
            var expected = new ConcurrentBag<string>();
            var stop = 0;

            // 克隆方不停把 Owners 抬起来，逼写方走「分离」路径。
            var cloner = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    _ = source.Clone();
                }
            });

            Parallel.For(0, 4, worker =>
            {
                for (var i = 0; i < 400; i++)
                {
                    var key = $"X-W{worker}-{i}";
                    source.Set(key, "v");
                    expected.Add(key);
                }
            });

            Volatile.Write(ref stop, 1);
            cloner.Wait();

            // 所有 Set 都作用在 source 上：source 自己必须看得到每一条，一条都不能少。
            foreach (var key in expected)
            {
                Assert.NotNull(source.Get(key));
            }
        }
    }

    [Fact]
    public void TransformList_ConcurrentCloneAndAppend_NeverLosesAppend()
    {
        for (var round = 0; round < 20; round++)
        {
            var config = new AxiosConfig();
            config.TransformRequest.Add(static (data, _) => data); // 让链非空，Clone 才会共享

            var stop = 0;
            var cloner = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    _ = config.Clone();
                }
            });

            const int appends = 1_600;
            Parallel.For(0, 4, _ =>
            {
                for (var i = 0; i < appends / 4; i++)
                {
                    config.TransformRequest.Add(static (data, _) => data);
                }
            });

            Volatile.Write(ref stop, 1);
            cloner.Wait();

            Assert.Equal(appends + 1, config.TransformRequest.Count);
        }
    }

    // ------------------------------------------------------------------
    // Clone 必须看到调用时刻的最新内容
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_AlwaysSeesLatestStore()
    {
        var source = new AxiosHeaders();
        source.Set("A", "1");
        _ = source.Clone(); // Owners 变 2

        source.Set("B", "2"); // 源必须先分离再写
        var clone = source.Clone();

        Assert.Equal("2", clone.Get("B"));
        Assert.Equal(2, clone.Count);
    }

    // ------------------------------------------------------------------
    // 零复制：无操作不该付整份复制的代价
    // ------------------------------------------------------------------

    [Fact]
    public void Headers_ClearOnSharedEmpty_DoesNotCopy()
    {
        var source = new AxiosHeaders();
        var shared = source.Clone(); // 共享一个空存储

        var cost = AverageBytes(20_000, source.Clear);

        // 复制实现：每次 new Store + new Dictionary（≈100 B）。快速路径：0 B。
        Assert.True(cost < 32, $"共享空存储上的 Clear() 平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(0, shared.Count);
    }

    [Fact]
    public void Headers_DeleteMissingOnShared_DoesNotCopy()
    {
        var source = NewHeaders(10);
        var shared = source.Clone();

        var cost = AverageBytes(20_000, () => source.Delete("X-Not-There"));

        Assert.True(cost < 32, $"删除不存在的头平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(10, shared.Count);
    }

    [Fact]
    public void TransformList_RemoveMissingOnShared_DoesNotCopy()
    {
        var config = new AxiosConfig();
        Func<object?, AxiosConfig, object?> transform = static (data, _) => data;
        config.TransformRequest.Add(transform);
        var shared = config.Clone();

        var cost = AverageBytes(20_000, () => config.TransformRequest.Remove(static (_, _) => null));

        Assert.True(cost < 32, $"移除不存在的 transform 平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(1, shared.TransformRequest.Count);
    }

    // ------------------------------------------------------------------
    // 动态节点的形状迁移缓存：字段名高度动态时也必须有界且正确
    // ------------------------------------------------------------------

    [Fact]
    public void Dynamic_ManyDistinctFieldNames_StillCorrect()
    {
        var node = new AxiosDynamic();

        // 2000 个互不重复的字段名：会越过单节点迁移缓存上限，之后派生不再登记。
        for (var i = 0; i < 2000; i++)
        {
            node[$"f{i}"] = i;
        }

        Assert.Equal(2000, node.Count);
        Assert.Equal(1999, (int)node["f1999"]!);

        for (var i = 0; i < 2000; i += 2)
        {
            Assert.True(node.Remove($"f{i}"));
        }

        Assert.Equal(1000, node.Count);
        Assert.Null(node["f0"]);
        Assert.Equal(1, (int)node["f1"]!);
    }

    // ------------------------------------------------------------------

    private static AxiosHeaders NewHeaders(int count)
    {
        var headers = new AxiosHeaders();
        for (var i = 0; i < count; i++)
        {
            headers.Set($"X-H{i}", $"value-{i}");
        }

        return headers;
    }

    private static long AverageBytes(int iterations, Action action)
    {
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }
}
