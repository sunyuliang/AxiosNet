using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 缓存条目的<b>导出</b>侧：自定义 <see cref="IAxiosCacheStore"/> 在 <c>SetAsync</c> 里必须能读到
/// 多值头全部值 / 落库时刻 / 落库龄期（<see cref="AxiosShortCircuit.MultiValueHeaderValues"/> /
/// <see cref="AxiosShortCircuit.CachedAt"/> / <see cref="AxiosShortCircuit.CachedAge"/>），
/// 否则「换 Redis / 共享缓存」只能存单值头，命中时静默降级。
/// </summary>
/// <remarks>
/// 读回侧（<see cref="AxiosShortCircuit.Restore"/>）由 <c>CacheEntryRestoreTests</c> 覆盖；本文件补四件事：
/// 保存侧读得到、导出视图确实是只读快照（写不到库内状态）、304 合并产出的条目单值头非空（且不改变响应），
/// 以及一次真正的「自己的格式存 → 自己的格式读回 → 命中」端到端等价。
/// </remarks>
public sealed class CacheEntryExportTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"name":"cached","age":7}""");

    /// <summary>响应头里的多值样本：<c>Vary</c> 两条、<c>X-Multi</c> 两条、<c>Age</c> 5 秒、带 <c>ETag</c>。</summary>
    private const string Url = "http://probe.invalid/export";

    /// <summary>
    /// 保存侧：<c>SetAsync</c> 收到的条目必须能导出多值头（含同名多条的全部值）、落库时刻与落库龄期，
    /// 且三项的数值与库自己算的一致（龄期就是 <see cref="AxiosCacheEntryPolicy.Age"/>）。
    /// </summary>
    [Fact]
    public async Task SetAsync_CanExportMultiValueHeadersAndTiming()
    {
        var store = new CapturingStore();
        var before = DateTimeOffset.UtcNow;

        using var client = new AxiosClient(
            new AxiosConfig
            {
                Caching = new AxiosCache(new AxiosCacheOptions { Store = store, TimeToLive = TimeSpan.FromMinutes(5) })
            },
            handler: new MultiValueHandler());

        var response = await client.GetAsync<Person>(Url);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("cached", response.Data!.Name);
        Assert.Equal(1, store.SetCalls);

        var entry = store.LastEntry!;

        // 多值头：每个头的全部值，同名两条都在。
        var exported = entry.MultiValueHeaderValues;
        Assert.NotNull(exported);
        Assert.Equal(new[] { "Accept", "Accept-Encoding" }, exported!["Vary"]);
        Assert.Equal(new[] { "one", "two" }, exported["X-Multi"]);

        // 单值字典同样要在（老存储按契约读它，取每个头的首值）。
        Assert.NotNull(entry.Headers);
        Assert.Equal("Accept", entry.Headers!["Vary"]);
        Assert.Equal("one", entry.Headers["X-Multi"]);

        // 落库时刻：必须是这一次请求的时间窗内。
        Assert.NotNull(entry.CachedAt);
        Assert.InRange(entry.CachedAt!.Value, before, after);

        // 落库龄期：就是响应里的 Age（5 秒），与库交给存储的 policy.Age 一致。
        Assert.Equal(TimeSpan.FromSeconds(5), entry.CachedAge);
        Assert.Equal(store.LastPolicy!.Age, entry.CachedAge!.Value);
    }

    /// <summary>
    /// 导出视图是<b>独立快照</b>，不是内部字典：<c>IReadOnlyDictionary</c> 只是接口约束，
    /// 若能强转回 <see cref="Dictionary{TKey,TValue}"/>（值还能强转回 <see cref="List{T}"/>），
    /// 存储实现者一次「顺手改一下」就会改到库内共享条目的状态。
    /// </summary>
    [Fact]
    public void MultiValueHeaderValues_IsAnIndependentReadOnlySnapshot()
    {
        var entry = AxiosShortCircuit.Restore(
            body: Body,
            headers: null,
            multiValueHeaders: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Vary"] = new[] { "Accept", "Accept-Encoding" },
                ["X-Multi"] = new[] { "one", "two" }
            });

        var view = entry.MultiValueHeaderValues!;

        // 不是内部字典实例：强转回可变字典拿不到写入能力。
        Assert.IsNotType<Dictionary<string, IEnumerable<string>>>(view);

        // 只读包装：写入被拒。
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, IEnumerable<string>>)view)["Vary"] = new[] { "nope" });

        // 值同样不是可变的内部 List<string>。
        Assert.False(view["X-Multi"] is List<string>, "导出的值列表不该暴露内部 List<string>。");

        // 两个快照互不共享可变状态：每次取都是本次的拷贝。
        Assert.NotSame(view, entry.MultiValueHeaderValues);
        Assert.Equal(new[] { "Accept", "Accept-Encoding" }, entry.MultiValueHeaderValues!["Vary"]);
        Assert.Equal(new[] { "one", "two" }, entry.MultiValueHeaderValues!["X-Multi"]);
    }

    /// <summary>
    /// 304 合并出来的条目必须<b>同时</b>带单值头与多值头：外部存储连单值头都拿不到就无从落库。
    /// 同时证明「补单值头不改变响应侧行为」—— 多值非空时库只用多值建响应头，单值字典整份忽略。
    /// </summary>
    [Fact]
    public async Task NotModifiedMerge_FillsSingleValueHeaders_WithoutChangingTheResponse()
    {
        var stored = AxiosShortCircuit.Restore(
            body: Body,
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Vary"] = "Accept",        // 单值字典只有首值
                ["ETag"] = "\"v1\"",
                ["Age"] = "5"
            },
            multiValueHeaders: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Vary"] = new[] { "Accept", "Accept-Encoding" },
                ["ETag"] = new[] { "\"v1\"" },
                ["Age"] = new[] { "5" }
            },
            receivedAt: DateTimeOffset.UtcNow,
            storedAge: TimeSpan.FromSeconds(5),
            contentType: AxiosContentType.Json);

        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Version = HttpVersion.Version11,

            // 304 自述的头（Content-Length 之类）：合并时必须丢弃，不能写进条目。
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        notModified.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
        notModified.Headers.TryAddWithoutValidation("X-Revalidated", "yes");

        var merged = InvokeMergeNotModified(stored, notModified);
        var mergedHeaders = merged.MultiValueHeaderValues;

        Assert.NotNull(mergedHeaders);
        Assert.False(mergedHeaders!.ContainsKey("Content-Length"), "304 自述的 Content-Length 不该进条目。");

        // 补上的单值字典：每个头的首值，与多值字典同源。
        Assert.NotNull(merged.Headers);
        foreach (var header in mergedHeaders)
        {
            Assert.Equal(header.Value.First(), merged.Headers![header.Key]);
        }

        Assert.Equal("yes", merged.Headers!["X-Revalidated"]);

        // 对照：同样的多值头、但没有单值字典 —— 这就是「补 Headers 之前」的形态。
        var withoutSingle = AxiosShortCircuit.Restore(
            body: merged.Body,
            headers: null,
            multiValueHeaders: mergedHeaders,
            receivedAt: merged.CachedAt,
            storedAge: merged.CachedAge,
            contentType: merged.ContentType,
            status: merged.Status,
            statusText: merged.StatusText);

        using var client = new AxiosClient(handler: new MultiValueHandler());

        var withHeaders = await client.GetAsync<Person>(Url, c => c.ShortCircuit = merged);
        var withoutHeaders = await client.GetAsync<Person>(Url, c => c.ShortCircuit = withoutSingle);

        // 响应头逐项一致（Age 会按各自的交付时刻推进，另行容忍 1 秒）。
        Assert.Equal(
            withoutHeaders.Headers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            withHeaders.Headers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        foreach (var header in withoutHeaders.Headers)
        {
            if (string.Equals(header.Key, "Age", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Equal(header.Value, withHeaders.Headers[header.Key]);
        }

        var ageWithHeaders = int.Parse(withHeaders.GetHeader("Age")!);
        var ageWithoutHeaders = int.Parse(withoutHeaders.GetHeader("Age")!);
        Assert.InRange(ageWithHeaders - ageWithoutHeaders, -1, 1);
    }

    /// <summary>
    /// 端到端等价（「换 Redis 不降级」的唯一证明）：自定义格式写出 → 读回 → 命中，
    /// 多值头 / <c>ETag</c> 与内置存储的命中结果一致，<c>Age</c> 继续推进；
    /// 只存单值头的退化存储则把 <c>Age</c> 冻在落库那一刻、并把 <c>Vary</c> 折叠成一条 —— 差异正是导出的三项带来的。
    /// </summary>
    [Fact]
    public async Task RoundTripThroughStoreFormat_KeepsTheHitEquivalentToTheBuiltInStore()
    {
        var roundTrip = new RoundTripStore();
        var degraded = new DegradedStore();

        using var builtInClient = NewClient(new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromMinutes(5) }));
        using var roundTripClient = NewClient(new AxiosCache(new AxiosCacheOptions
        {
            Store = roundTrip,
            TimeToLive = TimeSpan.FromMinutes(5)
        }));
        using var degradedClient = NewClient(new AxiosCache(new AxiosCacheOptions
        {
            Store = degraded,
            TimeToLive = TimeSpan.FromMinutes(5)
        }));

        var builtInFirst = await builtInClient.GetAsync<Person>(Url);
        var roundTripFirst = await roundTripClient.GetAsync<Person>(Url);
        var degradedFirst = await degradedClient.GetAsync<Person>(Url);

        Assert.Equal("cached", builtInFirst.Data!.Name);
        Assert.Equal("cached", roundTripFirst.Data!.Name);
        Assert.Equal("cached", degradedFirst.Data!.Name);
        Assert.Equal(1, roundTrip.Writes);

        // Age 按秒取整：让到缓存里停留的时间跨过 1 秒，退化与不退化才分得开。
        await Task.Delay(1100);

        var builtInHit = await builtInClient.GetAsync<Person>(Url);
        var roundTripHit = await roundTripClient.GetAsync<Person>(Url);
        var degradedHit = await degradedClient.GetAsync<Person>(Url);

        // 三条都是命中：没有发出请求。
        Assert.Null(builtInHit.Request);
        Assert.Null(roundTripHit.Request);
        Assert.Null(degradedHit.Request);

        // 1) 多值头：往返之后仍与内置存储逐项相同（Vary 的两条都在）。
        Assert.Equal(new[] { "Accept", "Accept-Encoding" }, roundTripHit.Headers["Vary"]);
        Assert.Equal(builtInHit.Headers["Vary"], roundTripHit.Headers["Vary"]);

        // 2) ETag：多值头这个「优先来源」没丢，值与首次响应一致。
        Assert.Equal("\"v1\"", roundTripHit.GetHeader("ETag"));
        Assert.Equal(builtInHit.GetHeader("ETag"), roundTripHit.GetHeader("ETag"));
        Assert.Equal(roundTripFirst.GetHeader("ETag"), roundTripHit.GetHeader("ETag"));

        // 3) Age：与内置存储同步推进；退化存储停在落库那一刻（5 秒）。
        var builtInAge = int.Parse(builtInHit.GetHeader("Age")!);
        var roundTripAge = int.Parse(roundTripHit.GetHeader("Age")!);
        var degradedAge = int.Parse(degradedHit.GetHeader("Age")!);

        Assert.True(builtInAge >= 6, $"内置存储的 Age 没推进：{builtInAge}s（落库龄期 5s + 停留 ≥1s）。");

        // 「已推进」必须显式断言：落库龄期是 5s，导出失败（CachedAt / CachedAge 读不到）时 Age 会停在 5。
        // 只靠下面那条 ±1 容差会放过它 —— builtInAge=6 时区间是 [5,7]，冻结值 5 正好在里面（假绿）。
        Assert.True(
            roundTripAge >= 6,
            $"往返之后 Age 没推进：{roundTripAge}s（落库龄期 5s + 停留 ≥1s；导出缺项会把它冻在 5s）。");
        Assert.InRange(roundTripAge, builtInAge - 1, builtInAge + 1);
        Assert.Equal(5, degradedAge);

        // 退化存储的另一半损失：只存单值头，Vary 被折叠成一条。
        Assert.Equal(new[] { "Accept" }, degradedHit.Headers["Vary"]);

        // 正文与状态与内置存储等价。
        Assert.Equal(builtInHit.Data!.Name, roundTripHit.Data!.Name);
        Assert.Equal(builtInHit.Status, roundTripHit.Status);
    }

    private static AxiosClient NewClient(AxiosCache cache)
        => new(new AxiosConfig { Caching = cache }, handler: new MultiValueHandler());

    /// <summary>
    /// 直连 <c>AxiosClient.MergeNotModified</c>（私有静态）：304 合并的产物只在库内流转，
    /// 包外（含 friend 测试程序集）够不到，只能反射调用 —— 改名即红，正好提醒重写本用例。
    /// </summary>
    /// <param name="stored">库里那条旧条目。</param>
    /// <param name="notModified">收到的 304 响应。</param>
    /// <returns>合并后的条目。</returns>
    private static AxiosShortCircuit InvokeMergeNotModified(AxiosShortCircuit stored, HttpResponseMessage notModified)
    {
        var method = typeof(AxiosClient).GetMethod("MergeNotModified", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "AxiosClient.MergeNotModified 不见了（改名 / 搬走）：本用例要跟着重写。");

        return (AxiosShortCircuit)method.Invoke(null, new object[] { stored, notModified })!;
    }

    /// <summary>响应内存正文 + 多条同名头（<c>Vary</c> / <c>X-Multi</c>）的桩 Handler：读路径同步完成。</summary>
    private sealed class MultiValueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version11,
                Content = content
            };

            // 同名多条：单值字典装不下，正是多值头的用武之地。
            response.Headers.TryAddWithoutValidation("Vary", new[] { "Accept", "Accept-Encoding" });
            response.Headers.TryAddWithoutValidation("X-Multi", new[] { "one", "two" });
            response.Headers.TryAddWithoutValidation("Age", "5");
            response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
            return Task.FromResult(response);
        }
    }

    /// <summary>把条目抄成一份记录，模拟「序列化到自己后端」：全部字段都经公开视图 / 公开属性取得。</summary>
    private abstract class RecordStore : IAxiosCacheStore
    {
        private readonly Dictionary<string, EntryRecord> _entries = new(StringComparer.Ordinal);

        /// <summary>被写入几次（用来证明确实走了存储，而不是命中别的东西）。</summary>
        public int Writes { get; private set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            var entry = _entries.TryGetValue(key, out var record) ? record.Restore() : null;
            return new ValueTask<AxiosShortCircuit?>(entry);
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => Store(key, entry, policy: null, cancellationToken);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
            => Store(key, entry, policy, cancellationToken);

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => new(_entries.Remove(key));

        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            _entries.Clear();
            return default;
        }

        /// <summary>单值头（每个头的首值）的复制：两种存储都从条目的公开属性抄。</summary>
        /// <param name="entry">库给的条目。</param>
        /// <returns>按大小写不敏感键复制的单值头字典。</returns>
        protected static Dictionary<string, string> CopySingle(AxiosShortCircuit entry)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry.Headers is { Count: > 0 } source)
            {
                foreach (var header in source)
                {
                    headers[header.Key] = header.Value;
                }
            }

            return headers;
        }

        /// <summary>子类决定「记录里留下什么」：这就是自定义存储格式的差别所在。</summary>
        /// <param name="entry">库给的条目。</param>
        /// <param name="policy">库算好的写入策略（老重载转发时为 <c>null</c>）。</param>
        /// <returns>要落进存储的记录。</returns>
        protected abstract EntryRecord Serialize(AxiosShortCircuit entry, AxiosCacheEntryPolicy? policy);

        private ValueTask Store(
            string key,
            AxiosShortCircuit entry,
            AxiosCacheEntryPolicy? policy,
            CancellationToken cancellationToken)
        {
            Writes++;
            _entries[key] = Serialize(entry, policy);
            return default;
        }
    }

    /// <summary>忠实往返的存储：多值头 / 落库时刻 / 落库龄期全部经导出视图抄进自己的记录。</summary>
    private sealed class RoundTripStore : RecordStore
    {
        protected override EntryRecord Serialize(AxiosShortCircuit entry, AxiosCacheEntryPolicy? policy)
        {
            var multi = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (entry.MultiValueHeaderValues is { Count: > 0 } exported)
            {
                foreach (var header in exported)
                {
                    multi[header.Key] = header.Value.ToArray();
                }
            }

            return new EntryRecord
            {
                Body = entry.Body,
                Status = entry.Status,
                StatusText = entry.StatusText,
                ContentType = entry.ContentType,
                Headers = CopySingle(entry),
                MultiValueHeaders = multi,
                ReceivedAt = entry.CachedAt,
                StoredAge = entry.CachedAge ?? policy?.Age
            };
        }
    }

    /// <summary>
    /// 退化存储（这次改造之前的形态）：只存单值头，不存多值头、不存落库时刻与龄期。
    /// 用它量出「不导出会丢什么」。
    /// </summary>
    private sealed class DegradedStore : RecordStore
    {
        protected override EntryRecord Serialize(AxiosShortCircuit entry, AxiosCacheEntryPolicy? policy)
            => new()
            {
                Body = entry.Body,
                Status = entry.Status,
                StatusText = entry.StatusText,
                ContentType = entry.ContentType,
                Headers = CopySingle(entry),
                MultiValueHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                ReceivedAt = null,
                StoredAge = null
            };
    }

    /// <summary>存储自己格式的一条记录（真实后端里就是一行 JSON / 一个哈希）。</summary>
    private sealed class EntryRecord
    {
        public byte[]? Body { get; init; }

        public int Status { get; init; }

        public string? StatusText { get; init; }

        public string? ContentType { get; init; }

        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> MultiValueHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset? ReceivedAt { get; init; }

        public TimeSpan? StoredAge { get; init; }

        public AxiosShortCircuit Restore()
            => AxiosShortCircuit.Restore(
                body: Body,
                headers: Headers,
                multiValueHeaders: MultiValueHeaders.Count > 0
                    ? MultiValueHeaders.ToDictionary(
                        header => header.Key,
                        header => (IEnumerable<string>)header.Value,
                        StringComparer.OrdinalIgnoreCase)
                    : null,
                receivedAt: ReceivedAt,
                storedAge: StoredAge,
                contentType: ContentType,
                status: Status,
                statusText: StatusText);
    }

    /// <summary>记录「库交给存储的那一条」的存储：SetAsync 里只看条目本身（模拟外部存储的视角）。</summary>
    private sealed class CapturingStore : IAxiosCacheStore
    {
        public AxiosShortCircuit? LastEntry { get; private set; }

        public AxiosCacheEntryPolicy? LastPolicy { get; private set; }

        public int SetCalls { get; private set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            // 库总是调用带 policy 的重载；老重载只是转发入口，这里只记下来备查。
            LastEntry = entry;
            return default;
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        {
            SetCalls++;
            LastEntry = entry;
            LastPolicy = policy;
            return default;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }
}
