
using AxiosNet.Tests.Infrastructure;
using System.Net;
using Xunit;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 请求头写时复制：克隆隔离、枚举快照、分配上限。
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class HeadersCopyOnWriteTests
{
    // ------------------------------------------------------------------
    // 语义：共享 → 写入时发布新版本
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
        // 方向必须对称：共享之后源自己写入也必须发布新版本，否则「先克隆、再改实例默认值」会打到已发出的请求上。
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
    public void Enumeration_AlwaysReadsCurrentVersion()
    {
        // 没有「快照缓存」这一层：每次枚举都直接取当前版本，写入天然生效。
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
        _ = CountOf(source); // 先枚举一次（拿的是那一刻的版本，不影响后续）

        source.Set("B", "2"); // 发布新版本
        var clone = source.Clone();

        Assert.Equal(2, clone.Count);
        Assert.Equal("2", clone.Get("B"));
    }

    /// <summary>
    /// 写时复制的<b>正确性前提</b>：写入只能发布新数组，绝不能就地改旧数组。
    /// <para>
    /// 用枚举器当「旧版本持有人」来验证：它捕获的是写入前那一版，之后无论 <c>Set</c> / <c>Delete</c>
    /// 怎么发布新版本，它看到的内容都必须原封不动。如果哪天有人为了省一次复制改成「原地扩容 + 改长度」，
    /// 这条用例会立刻变红——而按分配量写的护栏（<c>CloneThenSet_PaysExactlyOneNewVersion</c>）未必能发现。
    /// </para>
    /// </summary>
    [Fact]
    public void Enumerator_KeepsOldVersion_AfterWrites()
    {
        var headers = NewHeaders(2); // X-H0 / X-H1
        var walker = headers.GetEnumerator();
        Assert.True(walker.MoveNext());
        Assert.Equal("X-H0", walker.Current.Key);

        headers.Set("X-New", "1");
        Assert.True(headers.Delete("X-H1"));

        var rest = new List<string>();
        while (walker.MoveNext())
        {
            rest.Add(walker.Current.Key);
        }

        Assert.Equal(new[] { "X-H1" }, rest); // 旧数组没被就地改动
        Assert.Equal(2, headers.Count);       // 当前版本：X-H0 + X-New
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

        // 克隆只分配 AxiosHeaders 对象本身（约 24 B），与头数无关。
        Assert.True(
            largeCost < 300,
            $"10 个头的 AxiosHeaders.Clone() 平均 {largeCost} B/次，超出 300 B 护栏——疑似退回「整份复制」。");
        Assert.True(
            largeCost - smallCost < 100,
            $"克隆成本随头数增长（1 头 {smallCost} B，10 头 {largeCost} B）——写时复制失效。");
    }

    [Fact]
    public void EmptyHeaders_AllocatesInstanceOnly()
    {
        // 空 headers 很常见（懒创建的 Config.Headers、只读一次的默认值）：
        // 空版本是静态单例数组，构造只分配实例本身。
        var cost = AverageBytes(50_000, () => GC.KeepAlive(new AxiosHeaders()));

        Assert.True(cost < 100, $"new AxiosHeaders() 平均 {cost} B —— 疑似又为空集合预建了容器或缓存对象。");
    }

    [Fact]
    public void IdempotentSet_DoesNotPublishNewVersion()
    {
        // 「每请求把 Authorization / trace id 重设成同一个值」是常见形态。
        // 值没变就不发布新版本，只分配克隆实例本身。
        var source = NewHeaders(5);
        var name = "X-H0";
        var value = source.Get(name)!;

        var cost = AverageBytes(50_000, () =>
        {
            var copy = source.Clone();
            copy.Set(name, value);
            GC.KeepAlive(copy);
        });

        Assert.True(
            cost < 64,
            $"克隆后重设同一个值平均 {cost} B —— 幂等写入又发布了新版本（应当只有 24 B 的克隆实例）。");
    }

    [Fact]
    public void BulkConstruction_IsBuiltInOnePass()
    {
        var source = new Dictionary<string, string>();
        for (var i = 0; i < 10; i++)
        {
            source[$"X-H{i}"] = $"value-{i}";
        }

        // 构造走「攒进 List 再 ToArray」只成型一次，逐条 Set 会复制 N 次数组。
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

        // 枚举数组不应装箱；KeepAlive(int) 自身会装箱 24 B，故护栏留到 32 B。
        Assert.True(cost < 32, $"枚举 10 头平均 {cost} B —— 结构体枚举器没生效，又在装箱。");
    }

    [Fact]
    public void CloneThenSet_PaysExactlyOneNewVersion()
    {
        var source = NewHeaders(10);

        var cost = AverageBytes(20_000, () =>
        {
            var copy = source.Clone(); // 24 B（只共享引用）
            copy.Set("X-New", "1");    // 一次写入 = 一版新数组：11 × 24 B + 24 B 数组头
            GC.KeepAlive(copy);
        });

        // 约 312 B = 克隆 24 B + 新数组 288 B。
        // 预算留到 620 B 是给 Entry 布局变化留余量，不是精确基准。
        Assert.True(cost < 620, $"克隆 + 写一条平均 {cost} B —— 超出预算，疑似退化成整份深拷贝。");
    }

    [Fact]
    public async Task RequestWithDefaultHeaders_PaysOnlyBclHeaderCost()
    {
        // 默认头是「每请求拷一份、之后只读」的典型形态：5 个头不应让每请求多分配约 1.2 KB。
        // （≈240 B/头：整份克隆 + 每请求 List 快照）。写时复制之后我们只剩实例本身（24 B，与头数无关）。
        //
        // 剩下的大头是 BCL 把每个头写进 HttpRequestMessage.Headers —— 它按「字典初始化 + 扩容」计费，
        // 不是按头计费：net8 上 1 头 136 B / 5 头 288 B / 10 头 568 B，net6 上 1 头 296 B / 5 头 600 B。
        // 所以这里不用绝对阈值（换个运行时或改个头数，阈值就变成噪音），改为「现场测一遍裸 BCL 再相减」，
        // 只钉我们自己的那一份：AxiosNet 的边际成本 − 裸 BCL 的边际成本 ≤ 64 B。
        //
        // 两个成本必须同一口径测：都走进程级计数（见 MeasureRequestAsync）。混用「线程级 + 进程级」
        // 相减会得到一个既不是前者也不是后者的数，护栏照样会假红。
        using var bare = new AxiosClient(handler: new StubHandler());
        using var rich = new AxiosClient(handler: new StubHandler());
        for (var i = 0; i < DefaultHeaderNames.Length; i++)
        {
            rich.Defaults.Headers.Set(DefaultHeaderNames[i], DefaultHeaderValues[i]);
        }

        await Send(bare);
        await Send(rich);

        var bareCost = await MeasureRequestAsync(bare);
        var richCost = await MeasureRequestAsync(rich);
        var ours = richCost - bareCost;
        var bcl = MeasureBclHeaderCost();

        Assert.True(
            ours - bcl <= 64,
            $"{DefaultHeaderNames.Length} 个默认头让每请求多分配 {ours:N0} B，其中裸 BCL 自己就要 {bcl:N0} B ——"
            + $"我们这一份 {ours - bcl:N0} B，超出 64 B 护栏：疑似 Defaults.Clone 又变回整份复制，"
            + "或 ApplyHeaders 又每请求建 List。");
    }

    /// <summary>
    /// 对照基线：同一进程里「裸 BCL 把同样这几个头写进 HttpRequestMessage」的每请求成本。
    /// 头名 / 取值预存在静态数组里，测量循环内不做字符串插值，否则基线自身会被抬高。
    /// </summary>
    private static long MeasureBclHeaderCost()
    {
        var empty = AverageBytes(20_000, () => GC.KeepAlive(new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")));

        var withHeaders = AverageBytes(20_000, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/x");
            for (var i = 0; i < DefaultHeaderNames.Length; i++)
            {
                request.Headers.TryAddWithoutValidation(DefaultHeaderNames[i], DefaultHeaderValues[i]);
            }

            GC.KeepAlive(request);
        });

        return withHeaders - empty;
    }

    private static readonly string[] DefaultHeaderNames =
        Enumerable.Range(0, 5).Select(i => $"X-Default-{i}").ToArray();

    private static readonly string[] DefaultHeaderValues =
        Enumerable.Range(0, 5).Select(i => $"value-{i}").ToArray();

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

    /// <summary>
    /// 一次请求的平均分配字节数：进程级 <see cref="GC.GetTotalAllocatedBytes(bool)"/> 计数，
    /// 三轮取最小值以剔除 tier-1 JIT 等一次性分配；独占集合保证进程里没有别的分配源。
    /// </summary>
    private static async Task<long> MeasureRequestAsync(AxiosClient client)
    {
        const int iterations = 1_000;

        // 预热：把管线里的静态构造、字典扩容、tier-0 编译都付掉，不进测量窗口。
        for (var i = 0; i < 100; i++)
        {
            await client.GetAsync("http://localhost/x").ConfigureAwait(false);
        }

        var best = long.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < iterations; i++)
            {
                await client.GetAsync("http://localhost/x").ConfigureAwait(false);
            }

            best = Math.Min(best, (GC.GetTotalAllocatedBytes(precise: true) - before) / iterations);
        }

        return best;
    }

    /// <summary>测量轮数：每项测量跑几轮、取最小值（见 <see cref="MeasureRequestAsync"/> 的说明）。</summary>
    private const int MeasurementRounds = 3;

    /// <summary>
    /// 一段同步循环的「每次分配字节数」，口径与 <see cref="MeasureRequestAsync"/> 一致（进程级 + 取最小轮）。
    /// <para>用同一个计数器，才能把「我们的成本」与「裸 BCL 的成本」直接相减 —— 两个不同口径相减没有意义。</para>
    /// </summary>
    private static long AverageBytes(int iterations, Action action)
    {
        action();

        var best = long.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < iterations; i++)
            {
                action();
            }

            best = Math.Min(best, (GC.GetTotalAllocatedBytes(precise: true) - before) / iterations);
        }

        return best;
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