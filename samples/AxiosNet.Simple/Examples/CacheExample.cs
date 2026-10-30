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

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= new AxiosClient(new AxiosConfig { Cache = Cache });
        await Cache.ClearAsync();

        // 1) 没有缓存：每发一次，服务端的计数器就加一
        using var plain = new AxiosClient();
        var a = await plain.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
        var b = await plain.GetAsync<Dictionary<string, int>>($"{context.BaseUrl}cache/counter");
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
        using var etagClient = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions
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

        Console.WriteLine("  说明: Configured 是默认值（忽略请求与响应两侧的 HTTP 缓存指令）；只缓存 2xx 与拿到原始字节的响应；"
            + "Range 请求、206 部分响应、Vary: * 都不入缓存；流式响应、非 GET / HEAD 默认不入缓存");
        Console.WriteLine("  条件请求默认开启（ConditionalRequests = true）：ETag 优先，否则 Last-Modified；"
            + "调用方自己写条件头时以调用方为准（库不接管那次 304）");
    }

    /// <summary>Intersection 模式客户端：配置 TTL 10s，与服务端新鲜期取交集。</summary>
    private static AxiosClient NewIntersectionClient() => new(new AxiosConfig
    {
        Cache = new AxiosCache(new AxiosCacheOptions
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

    /// <summary>用指定模式连取两次（隔 <paramref name="delayMs"/> 毫秒），返回两次响应里的 served 计数。</summary>
    private static async Task<(int First, int Second)> ProbeAsync(string url, CacheFreshnessMode mode, int delayMs = 0)
    {
        // 客户端 TTL 给到 10 秒：Server / Intersection 是否提前失效由服务端新鲜期决定，Configured 则一直用这 10 秒
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions
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
}
