using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>36. 响应缓存：命中不发请求、到期自动失效、同键并发只发一次。</summary>
public sealed class CacheExample : IExample
{
    /// <summary>缓存与客户端都跨运行复用：每次运行先把条目清空，结果可复现。</summary>
    private static readonly AxiosCache Cache = new(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(2) });

    private static AxiosClient? _client;

    public int Number => 36;

    public string Title => "响应缓存：命中不发请求 / 到期失效 / 同键并发只发一次";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= new AxiosClient(new AxiosConfig { Cache = Cache });
        Cache.Clear();

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

        Console.WriteLine("  说明: 只缓存 2xx 与拿到原始字节的响应；流式响应、非 GET / HEAD 默认不入缓存");
    }
}
