using AxiosNet;
using AxiosNet.Cache;

namespace AxiosNet.Simple.Examples;

/// <summary>36. 响应缓存：命中不发请求、到期自动失效、同键并发只发一次、按服务器新鲜期失效、请求侧指令、ETag 条件复用。</summary>
public sealed class CacheExample : IExample
{
    /// <summary>缓存与客户端都跨运行复用：每次运行先把条目清空，结果可复现。</summary>
    private static readonly AxiosCache Cache = new(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(2) });

    private static AxiosClient? _client;

    public int Number => 36;

    public string Title => "响应缓存：命中不发请求 / 到期失效 / 同键并发只发一次 / 跟随服务器新鲜期 / 请求侧指令 / ETag 条件复用";

    /// <summary>示例入口：响应缓存：命中不发请求 / 到期失效 / 同键并发只发一次 / 跟随服务器新鲜期 / 请求侧指令 / ETag 条件复用。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= new AxiosClient(new AxiosConfig { Caching = Cache });
        await Cache.ClearAsync();

        // 1) 没有缓存：用 Axios 静态门面的默认客户端，每发一次，服务端的计数器就加一
        var a = await Axios.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        var b = await Axios.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        Console.WriteLine($"  无缓存: served = {a.Data!["served"]} -> {b.Data!["served"]}（每次都到服务端）");

        // 2) 有缓存：第二次拿的是缓存里的字节，计数器不动
        var first = await client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        var second = await client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        Console.WriteLine($"  有缓存: served = {first.Data!["served"]} -> {second.Data!["served"]}（第二次没发请求）");
        Console.WriteLine($"  命中判定: 第二次 Request = {(second.Request is null ? "null（未发送）" : "有请求消息")}");

        // 3) 同一份字节按调用形态重新解析：JSON 与原始文本各取所需
        var text = await client.GetAsync<string>($"{context.BaseUrl}cache/counter");
        Console.WriteLine($"  同一份字节解析成文本: {text.Data}");

        // 4) 单飞：10 个并发同键请求，服务端只被请求一次
        var before = await client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        var shared = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter?ms=300")));
        var after = await client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter?ms=300");
        Console.WriteLine($"  单飞: 10 个并发 -> served 从 {before.Data!["served"]} 变成 {after.Data!["served"]}（只发一次）");
        Console.WriteLine($"  10 个响应都拿到了数据: {shared.All(r => r.Data is { Count: > 0 })}");

        // 5) 到期失效：等到 TTL 之后再取，计数器继续增长
        await Task.Delay(2200);
        var expired = await client.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        Console.WriteLine($"  到期后: served = {expired.Data!["served"]}（重新发请求）");

        // 6) 三种新鲜度来源（FreshnessMode）：服务端声明 max-age=1，客户端配置 TTL=10s，1.3 秒后再取
        var ttlUrl = $"{context.BaseUrl}cache/ttl?max-age=1";
        var configured = await ProbeAsync(ttlUrl, CacheFreshnessMode.Configured, 1300);
        var serverTtl = await ProbeAsync(ttlUrl, CacheFreshnessMode.Server, 1300);
        var intersection = await ProbeAsync(ttlUrl, CacheFreshnessMode.Intersection, 1300);

        Console.WriteLine("  服务端 max-age=1，客户端 TTL=10s，1.3 秒后再取:");
        Console.WriteLine($"    Configured  : served {configured.First} -> {configured.Second}（仍命中：只信配置的 10s）");
        Console.WriteLine($"    Server      : served {serverTtl.First} -> {serverTtl.Second}（已过期：按服务器的 1s）");
        Console.WriteLine($"    Intersection: served {intersection.First} -> {intersection.Second}（已过期：取 min(10s, 1s)）");

        // 7) 服务器否决：no-store 在两种解析模式下不落库；Server 模式连「没声明新鲜期」的响应都不落库
        var noStoreUrl = $"{context.BaseUrl}cache/ttl?no-store=1";
        var configuredNoStore = await ProbeAsync(noStoreUrl, CacheFreshnessMode.Configured);
        var serverNoStore = await ProbeAsync(noStoreUrl, CacheFreshnessMode.Server);
        var serverNoHeaders = await ProbeAsync($"{context.BaseUrl}cache/counter", CacheFreshnessMode.Server);

        Console.WriteLine($"  no-store: Configured served {configuredNoStore.First} -> {configuredNoStore.Second}（仍命中），"
            + $"Server {serverNoStore.First} -> {serverNoStore.Second}（每次重发）");
        Console.WriteLine($"  Server + 没声明新鲜期（/cache/counter）: served {serverNoHeaders.First} -> {serverNoHeaders.Second}（不落库）");

        // 8) 请求侧指令：调用方能单方面收紧或绕开缓存（只在 Server / Intersection 下解析，Configured 一律忽略）
        var directiveUrl = $"{context.BaseUrl}cache/ttl?max-age=60";

        using var noCacheClient = NewIntersectionClient();
        var n1 = await FetchServedAsync(noCacheClient, directiveUrl);
        var n2 = await FetchServedAsync(noCacheClient, directiveUrl);
        var bypass1 = await FetchServedAsync(noCacheClient, directiveUrl, "no-cache");
        var bypass2 = await FetchServedAsync(noCacheClient, directiveUrl, "no-cache");
        var n3 = await FetchServedAsync(noCacheClient, directiveUrl);
        Console.WriteLine($"  no-cache: {n1} -> {n2}（命中），带 no-cache 两次 {bypass1} -> {bypass2}（都绕开查询、但照常刷新落库），之后 {n3}（命中刷新后的条目）");

        using var noStoreClient = NewIntersectionClient();
        var s1 = await FetchServedAsync(noStoreClient, directiveUrl, "no-store");
        var s2 = await FetchServedAsync(noStoreClient, directiveUrl, "no-store");
        var s3 = await FetchServedAsync(noStoreClient, directiveUrl);
        var s4 = await FetchServedAsync(noStoreClient, directiveUrl);
        Console.WriteLine($"  no-store: {s1} -> {s2}（既不查也不写，每次都发），恢复默认后 {s3} -> {s4}（先发一次再命中）");

        // 9) 条件请求：条目过期但带 ETag 时，客户端发 If-None-Match；服务端回 304，正文直接从缓存交付
        //    客户端自带一份缓存，每次运行新建以隔离条目
        using var etagClient = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                TimeToLive = TimeSpan.FromSeconds(10),
                FreshnessMode = CacheFreshnessMode.Server
            })
        });
        var etagUrl = $"{context.BaseUrl}cache/etag";
        var etagFirst = await etagClient.GetAsync<Dictionary<string, int>>(etagUrl);
        await Task.Delay(1200);   // 服务端声明 max-age=1，条目已过期
        var etagRevalidated = await etagClient.GetAsync<Dictionary<string, int>>(etagUrl);
        var etagHit = await etagClient.GetAsync<Dictionary<string, int>>(etagUrl);
        Console.WriteLine($"  条件请求: 首次 served = {etagFirst.Data!["served"]}；过期后重新验证 -> served = {etagRevalidated.Data!["served"]}"
            + $"（状态 {(int)etagRevalidated.Status}，Request {(etagRevalidated.Request is null ? "null（未发送）" : "已发出，304 复用缓存正文")}）；"
            + $"紧接着再取 Request = {(etagHit.Request is null ? "null（命中：304 已续上 TTL）" : "有请求消息")}");

        // 10) 自定义缓存后端：条目按「自己的记录」存，读回时用 AxiosShortCircuit.Restore 重建。
        //     存的时候从 MultiValueHeaderValues / CachedAt / CachedAge 三个只读视图把多值头、落库时刻、
        //     落库龄期一并抄进自己的记录格式；只存单值 Headers 会静默降级（不报错、行为变样）：
        //     多条 Set-Cookie / Vary 明细丢失、Age 停在落库那一刻、ETag 提取退化成「只有首值」。
        //     Restore 是这三个字段唯一的包外写入口 —— 换 Redis / 共享缓存时就是这个形状。
        var store = new RebuildingStore();
        using var customClient = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                TimeToLive = TimeSpan.FromSeconds(10),
                Store = store
            })
        });

        var rebuiltUrl = $"{context.BaseUrl}cache/etag";
        var rebuiltFirst = await customClient.GetAsync<Dictionary<string, int>>(rebuiltUrl);
        var rebuiltHit = await customClient.GetAsync<Dictionary<string, int>>(rebuiltUrl);
        Console.WriteLine($"  自定义存储: 首次 served = {rebuiltFirst.Data!["served"]} -> 命中 served = {rebuiltHit.Data!["served"]}"
            + $"（Request {(rebuiltHit.Request is null ? "null（命中，未发送）" : "已发出")}，"
            + $"ETag = {rebuiltHit.GetHeader("ETag") ?? "缺"}，重建 {store.RestoredCount} 次）");

        Console.WriteLine("  说明: Configured 是默认值（忽略请求与响应两侧的 HTTP 缓存指令）；只缓存 2xx 与拿到原始字节的响应；"
            + "Range 请求、206 部分响应、Vary: * 都不入缓存；流式响应、非 GET / HEAD 默认不入缓存");
        Console.WriteLine("  条件请求默认开启（ConditionalRequests = true）：ETag 优先，否则 Last-Modified；"
            + "调用方自己写条件头时以调用方为准（库不接管那次 304）");
    }

    /// <summary>Intersection 模式客户端：配置 TTL 10s，与服务端新鲜期取交集。缓存条目是本次运行的状态，故每次调用新建。</summary>
    private static AxiosClient NewIntersectionClient() => new(new AxiosConfig
    {
        Caching = new AxiosCache(new AxiosCacheOptions
        {
            TimeToLive = TimeSpan.FromSeconds(10),
            FreshnessMode = CacheFreshnessMode.Intersection
        })
    });

    /// <summary>取一次并读出服务端的 served 计数；<paramref name="cacheControl"/> 非空时给这次请求带上 Cache-Control。</summary>
    private static async Task<int> FetchServedAsync(AxiosClient client, string url, string? cacheControl = null)
    {
        var response = cacheControl is null
            ? await client.GetAsync<Dictionary<string, int>>(url)
            : await client.GetAsync<Dictionary<string, int>>(url, c => c.Headers.Set("Cache-Control", cacheControl));
        return response.Data!["served"];
    }

    /// <summary>用指定模式连取两次（隔 <paramref name="delayMs"/> 毫秒），返回两次响应里的 served 计数。客户端带着本次运行的缓存状态，故每次新建。</summary>
    private static async Task<(int First, int Second)> ProbeAsync(string url, CacheFreshnessMode mode, int delayMs = 0)
    {
        // 客户端 TTL 给到 10 秒：Server / Intersection 是否提前失效由服务端新鲜期决定，Configured 则一直用这 10 秒
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                TimeToLive = TimeSpan.FromSeconds(10),
                FreshnessMode = mode
            })
        });

        var first = await client.GetAsync<Dictionary<string, int>>(url);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        var second = await client.GetAsync<Dictionary<string, int>>(url);
        return (first.Data!["served"], second.Data!["served"]);
    }

    /// <summary>
    /// 演示用的「自定义缓存后端」：条目先转成自己的记录（正文字节 + 状态 + 单值头 + 多值头 + 落库时刻 + 龄期），
    /// 读回时用 <see cref="AxiosShortCircuit.Restore"/> 重建 —— 换 Redis / 共享缓存时就是这个形状。
    /// </summary>
    /// <remarks>
    /// 存的时候三个字段都有只读出口：<see cref="AxiosShortCircuit.MultiValueHeaderValues"/>（每个头的全部值）、
    /// <see cref="AxiosShortCircuit.CachedAt"/>（落库时刻）、<see cref="AxiosShortCircuit.CachedAge"/>
    /// （落库那一刻的龄期，等价于 <see cref="AxiosCacheEntryPolicy.Age"/>），所以能原样抄进自己的存储格式。
    /// <para>
    /// <b>只存单值 <see cref="AxiosShortCircuit.Headers"/> 会丢什么</b>：多条 <c>Set-Cookie</c> / <c>Vary</c>
    /// 除首值以外的全部值（折叠成一条会改变语义）、<c>ETag</c> / <c>Last-Modified</c> 的优先来源
    /// （只填单值字典时条件请求仍可用，但多值条目上的校验器会退化成「只有首值」），
    /// 以及 <c>Age</c> 的推进 —— 没有落库时刻与龄期，命中时 <c>Age</c> 就永远停在落库那一刻的数值。
    /// </para>
    /// </remarks>
    private sealed class RebuildingStore : IAxiosCacheStore
    {
        private readonly Dictionary<string, EntryRecord> _entries = new(StringComparer.Ordinal);

        /// <summary>被 <see cref="AxiosShortCircuit.Restore"/> 重建过几次（示例里用来证明走的是重建路径）。</summary>
        public int RestoredCount { get; private set; }

        /// <summary>取一条：没有记录返回 <c>null</c>；有则每次都从记录重建一个条目。</summary>
        /// <param name="key">缓存键。</param>
        /// <param name="cancellationToken">取消令牌（内存实现用不到）。</param>
        /// <returns>重建好的条目或 <c>null</c>。</returns>
        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            if (!_entries.TryGetValue(key, out var record))
            {
                return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
            }

            RestoredCount++;
            return new ValueTask<AxiosShortCircuit?>(record.Restore());
        }

        /// <summary>存一条（老重载，只给 TTL）：示例不做过期清理，条目永不过期 —— 这里只演示往返形状。</summary>
        /// <param name="key">缓存键。</param>
        /// <param name="entry">库给的条目：正文 / 状态 / 单值头都从这里取。</param>
        /// <param name="timeToLive">这条应有的存活时长。</param>
        /// <param name="cancellationToken">取消令牌（内存实现用不到）。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            _entries[key] = EntryRecord.From(entry, policy: null);
            return default;
        }

        /// <summary>存一条并带上库算好的策略：条目自己带了落库时刻与龄期就用它，否则退回 <see cref="AxiosCacheEntryPolicy.Age"/>。</summary>
        /// <param name="key">缓存键。</param>
        /// <param name="entry">库给的条目。</param>
        /// <param name="policy">写入策略（生效 TTL + HTTP 缓存信息）。</param>
        /// <param name="cancellationToken">取消令牌（内存实现用不到）。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        {
            _entries[key] = EntryRecord.From(entry, policy);
            return default;
        }

        /// <summary>摘掉一条，返回是否真的摘到了。</summary>
        /// <param name="key">缓存键。</param>
        /// <param name="cancellationToken">取消令牌（内存实现用不到）。</param>
        /// <returns>是否删除了记录。</returns>
        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => new(_entries.Remove(key));

        /// <summary>清空。</summary>
        /// <param name="cancellationToken">取消令牌（内存实现用不到）。</param>
        /// <returns>完成的任务。</returns>
        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            _entries.Clear();
            return default;
        }

        /// <summary>存储在「自己格式」里的一条记录：可序列化成任何后端的一条 JSON / 一个哈希。</summary>
        private sealed class EntryRecord
        {
            /// <summary>响应正文字节。</summary>
            public byte[]? Body { get; init; }

            /// <summary>状态码。</summary>
            public int Status { get; init; }

            /// <summary>状态码文案。</summary>
            public string? StatusText { get; init; }

            /// <summary>正文媒体类型。</summary>
            public string? ContentType { get; init; }

            /// <summary>单值头（每个头的首值）：老派的存储 / 只读首值的后端用它。</summary>
            public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>多值头（每个头的全部值）：多条 <c>Set-Cookie</c> / <c>Vary</c> 只能这样存。</summary>
            public Dictionary<string, string[]> MultiValueHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>落库时刻：命中时用它把 <c>Age</c> 推进到当前龄期。</summary>
            public DateTimeOffset? ReceivedAt { get; init; }

            /// <summary>落库那一刻的龄期（RFC 9111 的 current_age）。</summary>
            public TimeSpan? StoredAge { get; init; }

            /// <summary>从库给的条目抄一份记录；三个内部字段经只读视图导出，一项都不落下。</summary>
            /// <param name="entry">库给的条目。</param>
            /// <param name="policy">库算好的写入策略；老重载（只有 TTL）传 <c>null</c>。</param>
            /// <returns>可持久化的记录。</returns>
            public static EntryRecord From(AxiosShortCircuit entry, AxiosCacheEntryPolicy? policy)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.Headers is { Count: > 0 } source)
                {
                    foreach (var header in source)
                    {
                        if (IsSessionCookie(header.Key))
                        {
                            continue;
                        }

                        headers[header.Key] = header.Value;
                    }
                }

                // 全部值：条目只给了只读视图，这里按自己的格式复制成数组（模拟序列化到后端）。
                var multiValueHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (entry.MultiValueHeaderValues is { Count: > 0 } multi)
                {
                    foreach (var header in multi)
                    {
                        if (IsSessionCookie(header.Key))
                        {
                            continue;
                        }

                        multiValueHeaders[header.Key] = header.Value.ToArray();
                    }
                }

                return new EntryRecord
                {
                    Body = entry.Body,
                    Status = entry.Status,
                    StatusText = entry.StatusText,
                    ContentType = entry.ContentType,
                    Headers = headers,
                    MultiValueHeaders = multiValueHeaders,

                    // 条目自己带了落库时刻 / 龄期就照抄（库落库时填的），拿不到才退回策略里的龄期。
                    ReceivedAt = entry.CachedAt,
                    StoredAge = entry.CachedAge ?? policy?.Age
                };
            }

            /// <summary>
            /// 会话 Cookie 头：<b>不入缓存</b>。库自己的落库路径也这样过滤（<c>AxiosClient.Cache</c> 的
            /// <c>IsSessionCookieHeader</c>）—— 缓存命中会把它回放给下一次请求，共享缓存下等于把别人的会话
            /// 塞进来。自建存储必须自己守这条，库不会再替你拦。
            /// </summary>
            /// <param name="name">头名。</param>
            /// <returns>是会话 Cookie 头返回 <c>true</c>。</returns>
            private static bool IsSessionCookie(string name)
                => string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Set-Cookie2", StringComparison.OrdinalIgnoreCase);

            /// <summary>用 <see cref="AxiosShortCircuit.Restore"/> 把记录装回一条缓存条目。</summary>
            /// <returns>可直接当作命中结果交付的条目。</returns>
            public AxiosShortCircuit Restore()
                => AxiosShortCircuit.Restore(
                    body: Body,
                    headers: Headers,

                    // 多值字典非空时库只用它建响应头，单值字典整份忽略：两者同源，行为与原来一致。
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
    }
}
