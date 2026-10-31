using System.Diagnostics;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>29. 短路响应：不发请求、直接交付调用方给的响应。</summary>
public sealed class ShortCircuitExample : IExample
{
    /// <summary>拦截器缓存：跨运行复用，每次运行开始清空。</summary>
    private static readonly Dictionary<string, AxiosShortCircuit> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>客户端静态复用：请求 / 响应两个拦截器在创建时挂一次。</summary>
    private static AxiosClient? _client;

    public int Number => 29;

    public string Title => "短路响应：缓存命中不发请求 / 一份字节多形态 / 拦截器接缓存 / mock / 错误语义";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= CreateClient(context.BaseUrl);
        Cache.Clear();

        // 缓存条目 = 状态码 + Content-Type + 原始字节（先用 Real 响应取一份）
        var body = (await client.GetAsync<byte[]>("users/1", c => c.ResponseType = ResponseType.ArrayBuffer)).Data!;

        var entry = new AxiosShortCircuit
        {
            Status = 200,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["X-Cache"] = "HIT" },
            Body = body
        };

        // 1) 有没有真的省掉网络：同一个 300ms 的接口，直发 vs 短路
        var stopwatch = Stopwatch.StartNew();
        await client.GetAsync("slow?ms=300");
        var direct = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        var cached = await client.GetAsync("slow?ms=300", c =>
            c.ShortCircuit = AxiosShortCircuit.Json("""{"delayedMs":300}"""));
        var shortCircuited = stopwatch.ElapsedMilliseconds;

        Console.WriteLine($"  直发 {direct}ms  vs  短路 {shortCircuited}ms（同样的正文，后者没有发出请求）");
        Console.WriteLine($"  短路响应的状态：{cached.Status} {cached.StatusText}，Request = {(cached.Request is null ? "null" : "非 null")}");

        // 2) 同一份字节喂给两个泛型入口：形状跟着本次调用变
        var typed = await client.GetAsync<User>("users/1", c => c.ShortCircuit = entry);
        var text = await client.GetAsync<string>("users/1", c => c.ShortCircuit = entry);

        Console.WriteLine($"  GetAsync<User>  → {typed.Data!.Name}, {typed.Data.Age} 岁，X-Cache={typed.GetHeader("X-Cache")}");
        Console.WriteLine($"  GetAsync<string> → {Shorten(text.Data!)}");

        // 3) 拦截器接一层缓存：请求拦截器命中就不发，响应拦截器负责写入（两个拦截器在 CreateClient 里挂好）
        var miss = await client.GetAsync<byte[]>("users/1?from=cache", c => c.ResponseType = ResponseType.ArrayBuffer);

        stopwatch.Restart();
        var hit = await client.GetAsync<User>("users/1?from=cache");
        stopwatch.Stop();

        Console.WriteLine(
            $"  第一次（未命中）发了 {miss.Data!.Length} 字节；第二次（命中）{stopwatch.ElapsedMilliseconds}ms，"
            + $"{hit.Data!.Name} 来自缓存");
        Console.WriteLine($"  缓存条目数：{Cache.Count}");

        // 4) mock：Data 直接给，不解析也不跑 transformResponse
        var stub = new User(99, "stub", 0, false, 0, null, null);
        var mocked = await client.GetAsync<User>("users/1", c => c.ShortCircuit = AxiosShortCircuit.From(stub));

        Console.WriteLine($"  mock：{mocked.Data!.Name}（Data 原样交付）");

        // 5) 错误语义一致：非 2xx 照样走 validateStatus → AxiosException
        try
        {
            await client.GetAsync("users/1", c => c.ShortCircuit = new AxiosShortCircuit
            {
                Status = 404,
                ContentType = "application/json; charset=utf-8",
                Body = System.Text.Encoding.UTF8.GetBytes("""{"message":"not found"}""")
            });
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  404 短路：code={ex.Code}, status={ex.Status}（与真实 404 同一个错误模型）");
        }

        Console.WriteLine("  说明: 短路不占并发名额、不进重试与熔断、不产生遥测 span；反序列化 / transformResponse / 响应拦截器照常");
    }

    /// <summary>
    /// 创建静态复用的客户端，并挂上「请求拦截器查缓存 / 响应拦截器写缓存」这一层。
    /// 拦截器只挂一次，避免跨运行重复叠加。
    /// </summary>
    private static AxiosClient CreateClient(string baseUrl)
    {
        var client = new AxiosClient(new AxiosConfig { BaseAddress = baseUrl });

        client.Interceptors.Request.Use(config =>
        {
            if (config.Url is { } url && Cache.TryGetValue(url, out var hit))
            {
                config.ShortCircuit = hit;
            }

            return config;
        });

        client.Interceptors.Response.Use(response =>
        {
            // 只缓存拿得到字节的响应
            if (response.Data is byte[] bytes && response.Config?.Url is { } url)
            {
                Cache[url] = new AxiosShortCircuit
                {
                    Status = response.Status,
                    ContentType = response.GetHeader("Content-Type"),
                    Body = bytes
                };
            }

            return response;
        });

        return client;
    }

    /// <summary>正文太长时截断显示（示例输出保持一行）。</summary>
    private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "…";
}
