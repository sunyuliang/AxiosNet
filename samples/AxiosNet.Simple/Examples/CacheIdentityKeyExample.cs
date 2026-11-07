using System.Security.Cryptography;
using System.Text;
using AxiosNet;
using AxiosNet.Cache;

namespace AxiosNet.Simple.Examples;

/// <summary>38. 缓存身份键：自定义头 / 查询串令牌的正解，以及 OnSendingRequest 的反例。</summary>
public sealed class CacheIdentityKeyExample : IExample
{
    public int Number => 38;

    public string Title => "缓存身份键：X-Api-Key / 查询串令牌 / OnSendingRequest 的边界";

    /// <summary>示例入口：缓存身份键：X-Api-Key / 查询串令牌 / OnSendingRequest 的边界。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        await ApiKeyScenarioAsync(context);
        await QueryTokenScenarioAsync(context);
        await OnSendingRequestScenarioAsync(context);
    }

    /// <summary>
    /// 适用场景：身份放在非标准头（如 X-Api-Key）里。
    /// 默认键只认 Authorization / Transport.Auth，必须把该头摘要进 KeySelector；
    /// 没有该头时回落到 AxiosCache.CreateKey，保证匿名请求仍按 URL 分区。
    /// </summary>
    private static async Task ApiKeyScenarioAsync(ExampleContext context)
    {
        Console.WriteLine("  [1] 自定义身份头 X-Api-Key：正确配 KeySelector");

        var cache = new AxiosCache(new AxiosCacheOptions
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            KeySelector = ApiKeyKeySelector
        });
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl, Caching = cache });

        var alice = await client.GetAsync<Dictionary<string, string>>("headers", c => c.Headers.Set("X-Api-Key", "alice"));
        var bob = await client.GetAsync<Dictionary<string, string>>("headers", c => c.Headers.Set("X-Api-Key", "bob"));
        var aliceAgain = await client.GetAsync<Dictionary<string, string>>("headers", c => c.Headers.Set("X-Api-Key", "alice"));

        Console.WriteLine($"      alice -> x-api-key={alice.Data!["x-api-key"]}");
        Console.WriteLine($"      bob   -> x-api-key={bob.Data!["x-api-key"]}（另一个键，没有命中 alice）");
        Console.WriteLine($"      alice -> x-api-key={aliceAgain.Data!["x-api-key"]}（Request = {(aliceAgain.Request is null ? "null（命中缓存）" : "有请求消息")}）");
        Console.WriteLine("      键：" + cache.KeyFor(ConfigWithHeader("X-Api-Key", "alice"), new Uri(context.BaseUrl + "headers")));
    }

    /// <summary>
    /// 适用场景：令牌只能放查询串（如第三方回调）。
    /// 默认键会原样把 ?token= 明文写进键和审计 URI；这里先剔除 token，
    /// 再只把它的摘要追加进键，既保留身份隔离又不泄露原值。
    /// </summary>
    private static async Task QueryTokenScenarioAsync(ExampleContext context)
    {
        Console.WriteLine("  [2] 查询串令牌：默认键明文 vs KeySelector 摘要");

        var aliceUri = new Uri(context.BaseUrl + "cache/counter?token=alice");
        var bobUri = new Uri(context.BaseUrl + "cache/counter?token=bob");
        Console.WriteLine($"      默认键（明文）: {AxiosCache.CreateKey(new AxiosConfig(), aliceUri)}");
        Console.WriteLine($"      安全键（摘要）: {QueryTokenKeySelector(new AxiosConfig(), aliceUri)}");

        var cache = new AxiosCache(new AxiosCacheOptions
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            KeySelector = QueryTokenKeySelector
        });
        using var client = new AxiosClient(new AxiosConfig { Caching = cache });

        var alice = await client.GetAsync<Dictionary<string, int>>(aliceUri.AbsoluteUri);
        var bob = await client.GetAsync<Dictionary<string, int>>(bobUri.AbsoluteUri);
        var aliceAgain = await client.GetAsync<Dictionary<string, int>>(aliceUri.AbsoluteUri);

        Console.WriteLine($"      served: alice={alice.Data!["served"]} -> bob={bob.Data!["served"]} -> alice={aliceAgain.Data!["served"]}（第三次命中）");
    }

    /// <summary>
    /// 反例：把身份头只写在 OnSendingRequest 里。
    /// 缓存键在发送前就已算好，钩子晚于建键执行，所以 KeySelector 也看不到这个头；
    /// 两个身份会共用同一条缓存。要分区请改在按请求配置或请求拦截器里写头。
    /// </summary>
    private static async Task OnSendingRequestScenarioAsync(ExampleContext context)
    {
        Console.WriteLine("  [3] 反例：身份头只在 OnSendingRequest 里写");

        var cache = new AxiosCache(new AxiosCacheOptions
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            KeySelector = ApiKeyKeySelector
        });
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl, Caching = cache });

        var alice = await client.GetAsync<Dictionary<string, string>>("headers", c =>
            c.Observability.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.Remove("X-Api-Key");
                request.Headers.TryAddWithoutValidation("X-Api-Key", "alice");
                return ValueTask.CompletedTask;
            });
        var bob = await client.GetAsync<Dictionary<string, string>>("headers", c =>
            c.Observability.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.Remove("X-Api-Key");
                request.Headers.TryAddWithoutValidation("X-Api-Key", "bob");
                return ValueTask.CompletedTask;
            });

        var aliceKey = cache.KeyFor(Hooked("alice"), new Uri(context.BaseUrl + "headers"));
        var bobKey = cache.KeyFor(Hooked("bob"), new Uri(context.BaseUrl + "headers"));

        Console.WriteLine($"      alice -> x-api-key={alice.Data!["x-api-key"]}");
        Console.WriteLine($"      bob   -> x-api-key={bob.Data!["x-api-key"]}（bob 的钩子没进键，命中了 alice）");
        Console.WriteLine($"      两把键相同 = {aliceKey == bobKey}");
    }

    /// <summary>可直接复用：把 X-Api-Key 摘要进键，没有该头时回落到默认键（匿名请求仍按 URL 分区）。</summary>
    private static string ApiKeyKeySelector(AxiosConfig config, Uri uri)
    {
        var apiKey = config.Headers.Get("X-Api-Key");
        return string.IsNullOrWhiteSpace(apiKey)
            ? AxiosCache.CreateKey(config, uri)
            : AxiosCache.CreateKey(config, uri) + " ak:" + Digest(apiKey);
    }

    /// <summary>可直接复用：剔除查询串里的 token，只把摘要追加进键。</summary>
    private static string QueryTokenKeySelector(AxiosConfig config, Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return AxiosCache.CreateKey(config, uri);
        }

        var kept = new List<string>();
        string? token = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var name = equals < 0 ? part : part[..equals];
            if (name.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                token = equals < 0 ? string.Empty : Uri.UnescapeDataString(part[(equals + 1)..]);
            }
            else
            {
                kept.Add(part);
            }
        }

        var sanitized = new UriBuilder(uri) { Query = string.Join("&", kept) }.Uri;
        var key = AxiosCache.CreateKey(config, sanitized);
        return string.IsNullOrEmpty(token) ? key : key + " tk:" + Digest(token);
    }

    private static AxiosConfig ConfigWithHeader(string name, string value)
    {
        var config = new AxiosConfig();
        config.Headers.Set(name, value);
        return config;
    }

    private static AxiosConfig Hooked(string apiKey)
    {
        var config = new AxiosConfig();
        config.Observability.OnSendingRequest = (request, _, _) =>
        {
            request.Headers.Remove("X-Api-Key");
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            return ValueTask.CompletedTask;
        };
        return config;
    }

    /// <summary>与库内身份摘要同口径：SHA-256 前 8 字节转十六进制。</summary>
    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));
}
