using System.Net;
using Xunit;

namespace AxiosNet.Tests;

/// <summary>
/// <see cref="AxiosHeaders"/> 的「写时复制」：默认头每请求都要克隆一份，但绝大多数请求从不改它，
/// 所以克隆只共享存储（引用计数 +1），直到任何一方写入才真正复制字典。
/// <list type="bullet">
/// <item>语义护栏：分离是对称的（克隆方写入不影响源，源写入也不影响副本），快照失效正确；</item>
/// <item>分配护栏：克隆成本与头数无关；每请求链路不为「默认头」付按头计的分配。</item>
/// </list>
/// 阈值均留一倍以上余量，是用例「数量级不退回」的护栏，不是精确基准。
/// </summary>
public sealed class HeadersCopyOnWriteTests
{
    // ------------------------------------------------------------------
    // 语义：共享 → 写入时分离
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_ReadsSharedValues()
    {
        var source = new AxiosHeaders();
        source.Set("Authorization", "Bearer a");

        var clone = source.Clone();

        Assert.NotSame(source, clone);
        Assert.Equal("Bearer a", clone.Get("Authorization"));
        Assert.Equal(1, clone.Count);
    }

    [Fact]
    public void Clone_WriteOnClone_DoesNotAffectSource()
    {
        var source = new AxiosHeaders();
        source.Set("Accept", "application/json");

        var clone = source.Clone();
        clone.Set("Accept", "text/plain");
        clone.Set("X-Only-In-Clone", "1");

        Assert.Equal("application/json", source.Get("Accept"));
        Assert.Null(source.Get("X-Only-In-Clone"));
        Assert.Equal("text/plain", clone.Get("Accept"));
        Assert.Equal(2, clone.Count);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public void Clone_WriteOnSource_DoesNotAffectClone()
    {
        // 方向必须对称：共享之后源自己也必须先分离，否则「先克隆、再改实例默认值」会打到已发出的请求上。
        var source = new AxiosHeaders();
        source.Set("Accept", "application/json");

        var clone = source.Clone();
        source.Set("Accept", "text/plain");
        source.Set("X-Late", "late");

        Assert.Equal("application/json", clone.Get("Accept"));
        Assert.Null(clone.Get("X-Late"));
    }

    [Fact]
    public void Clone_DeleteOnClone_KeepsSourceEntry()
    {
        var source = new AxiosHeaders();
        source.Set("X-Trace", "abc");

        var clone = source.Clone();
        Assert.True(clone.Delete("X-Trace"));

        Assert.Equal("abc", source.Get("X-Trace"));
        Assert.Null(clone.Get("X-Trace"));

        clone.Clear();
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public void Snapshot_IsInvalidatedByLaterWrites()
    {
        // 快照数组是缓存的：任何写入都必须让它失效，否则「先枚举一次再改」会读到旧值。
        var headers = new AxiosHeaders();
        headers.Set("A", "1");
        Assert.Equal("1", Assert.Single(headers).Value);

        headers.Set("A", "2");
        Assert.Equal("2", Assert.Single(headers).Value);

        headers.Set("B", "3");
        Assert.Equal(2, CountOf(headers));

        Assert.True(headers.Delete("A"));
        var remaining = Assert.Single(headers);
        Assert.Equal("B", remaining.Key);

        headers.Clear();
        Assert.Empty(headers);
    }

    [Fact]
    public void Clone_AfterEnumeration_StillSeesUpdatesMadeBeforeClone()
    {
        var source = new AxiosHeaders();
        source.Set("A", "1");
        _ = CountOf(source); // 建出快照缓存

        source.Set("B", "2"); // 让缓存失效
        var clone = source.Clone();

        Assert.Equal(2, clone.Count);
        Assert.Equal("2", clone.Get("B"));
    }

    [Fact]
    public void ConcurrentWritesOnClones_StayIsolated()
    {
        var source = new AxiosHeaders();
        source.Set("X-Default", "v");

        const int workers = 8;
        var clones = new AxiosHeaders[workers];
        for (var i = 0; i < workers; i++)
        {
            clones[i] = source.Clone();
        }

        Parallel.For(0, workers, i =>
        {
            var clone = clones[i];
            for (var n = 0; n < 500; n++)
            {
                clone.Set($"X-W{i}", n.ToString());
                clone.Set("X-Shared-Key", i.ToString());
                _ = clone.Get("X-Default");
                _ = CountOf(clone);
            }
        });

        for (var i = 0; i < workers; i++)
        {
            Assert.Equal(i.ToString(), clones[i].Get("X-Shared-Key"));
            Assert.Equal($"499", clones[i].Get($"X-W{i}"));
            Assert.Equal("v", clones[i].Get("X-Default"));
        }

        // 源始终只有自己那一条
        Assert.Equal(1, source.Count);
        Assert.Equal("v", source.Get("X-Default"));
    }

    [Fact]
    public void ConfigClone_ModifyingSourceHeaders_DoesNotAffectClone()
    {
        var source = new AxiosConfig();
        source.Headers.Set("Accept", "application/json");

        var clone = source.Clone();
        source.Headers.Set("Accept", "text/plain");
        clone.Headers.Set("X-Clone", "1");

        Assert.Equal("text/plain", source.Headers.Get("Accept"));
        Assert.Equal("application/json", clone.Headers.Get("Accept"));
        Assert.Null(source.Headers.Get("X-Clone"));
    }

    // ------------------------------------------------------------------
    // 分配：克隆成本与头数无关
    // ------------------------------------------------------------------

    [Fact]
    public void Clone_CostDoesNotScaleWithHeaderCount()
    {
        var small = NewHeaders(1);
        var large = NewHeaders(10);

        var smallCost = AverageBytes(50_000, () => small.Clone());
        var largeCost = AverageBytes(50_000, () => large.Clone());

        // 复制实现：克隆 10 头的成本 ≈ 10 × 单条（实测 1 头 388 B、10 头 1312 B）。
        // 写时复制：只分配 AxiosHeaders 对象本身（实测 24 B），与头数无关。
        Assert.True(
            largeCost < 300,
            $"10 个头的 AxiosHeaders.Clone() 平均 {largeCost} B/次，超出 300 B 护栏——疑似退回「整份复制」。");
        Assert.True(
            largeCost - smallCost < 100,
            $"克隆成本随头数增长（1 头 {smallCost} B，10 头 {largeCost} B）——写时复制失效。");
    }

    [Fact]
    public void EmptyHeaders_DoesNotPreallocateDictionary()
    {
        // 空 headers 很常见（懒创建的 Config.Headers、只读一次的默认值）：
        // 旧实现构造时就建 Store + 锁对象 + 空字典（176 B），现在只有实例 + 惰性存储（实测 64 B）。
        var cost = AverageBytes(50_000, () => GC.KeepAlive(new AxiosHeaders()));

        Assert.True(cost < 100, $"new AxiosHeaders() 平均 {cost} B —— 疑似又预建了空字典或独立锁对象。");
    }

    [Fact]
    public void BulkConstruction_DoesNotGrowDictionaryRepeatedly()
    {
        var source = new Dictionary<string, string>();
        for (var i = 0; i < 10; i++)
        {
            source[$"X-H{i}"] = $"value-{i}";
        }

        // 逐条 Set 会让字典按 3/5/9/17 反复扩容（实测 1144 B）；按已知数量预分配后 560 B。
        var cost = AverageBytes(20_000, () => GC.KeepAlive(new AxiosHeaders(source)));

        Assert.True(cost < 700, $"构造 10 头 AxiosHeaders 平均 {cost} B —— 疑似又逐条扩容。");
    }

    [Fact]
    public void Enumeration_DoesNotBoxEnumerator()
    {
        var headers = NewHeaders(10);
        _ = CountOf(headers); // 先把快照建好，之后枚举应零分配

        var cost = AverageBytes(50_000, () =>
        {
            var count = 0;
            foreach (var _ in headers)
            {
                count++;
            }

            GC.KeepAlive(count);
        });

        // 旧实现把数组枚举器装箱（每次 56 B）。注意 KeepAlive(int) 自身会装箱 24 B，故护栏留到 32 B。
        Assert.True(cost < 32, $"枚举 10 头平均 {cost} B —— 结构体枚举器没生效，又在装箱。");
    }

    [Fact]
    public void Detach_StaysWithinBudget()
    {
        var source = NewHeaders(10);

        var cost = AverageBytes(20_000, () =>
        {
            var copy = source.Clone(); // 24 B
            copy.Set("X-New", "1");    // 首次写入触发分离：复制 10 条字典
            GC.KeepAlive(copy);
        });

        // 分离 = 复制一份字典（10 条）+ 一个新存储，实测 504 B；整份「克隆后再深拷贝」的旧形态是 1312 B 起。
        Assert.True(cost < 620, $"克隆 + 分离写一条平均 {cost} B —— 超出预算，疑似退化成整份深拷贝。");
    }

    [Fact]
    public async Task RequestWithDefaultHeaders_DoesNotPayPerHeaderAllocation()
    {
        // 默认头是「每请求拷一份、之后只读」的典型形态：
        // 修复前 5 个默认头让每个请求多分配 1200 B（≈240 B/头，克隆 + 每请求 List 快照）；
        // 修复后只剩 BCL 自己把 5 个头写进 HttpRequestMessage 的开销（实测 360 B，≈72 B/头）。
        using var bare = new AxiosClient(handler: new StubHandler());
        using var rich = new AxiosClient(handler: new StubHandler());
        for (var i = 0; i < 5; i++)
        {
            rich.Defaults.Headers.Set($"X-Default-{i}", $"value-{i}");
        }

        await Send(bare);
        await Send(rich);

        var bareCost = MeasureRequest(bare);
        var richCost = MeasureRequest(rich);

        var delta = richCost - bareCost;
        Assert.True(
            delta < 700,
            $"5 个默认头让每个请求多分配 {delta:N0} B（{bareCost:N0} → {richCost:N0}）——"
            + "超出 700 B 护栏，疑似 Defaults.Clone 又变回整份复制或 ApplyHeaders 又每请求建 List。");
    }

    private static AxiosHeaders NewHeaders(int count)
    {
        var headers = new AxiosHeaders();
        for (var i = 0; i < count; i++)
        {
            headers.Set($"X-H{i}", $"value-{i}");
        }

        return headers;
    }

    private static int CountOf(AxiosHeaders headers)
    {
        var count = 0;
        foreach (var _ in headers)
        {
            count++;
        }

        return count;
    }

    private static Task Send(AxiosClient client) => client.GetAsync("http://localhost/x");

    private static long MeasureRequest(AxiosClient client)
    {
        const int iterations = 2_000;
        for (var i = 0; i < 100; i++)
        {
            client.GetAsync("http://localhost/x").GetAwaiter().GetResult();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            client.GetAsync("http://localhost/x").GetAwaiter().GetResult();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
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

    /// <summary>不碰网络的 Handler：响应固定，便于确定性地测量配置与头部开销。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
