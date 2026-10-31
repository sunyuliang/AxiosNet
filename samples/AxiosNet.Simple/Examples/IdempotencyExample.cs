using AxiosNet;
using AxiosNet.Retry;

namespace AxiosNet.Simple.Examples;

/// <summary>35. 幂等键：一个逻辑请求一个键，重试的各次发送带同一个键；显式键可透传上游。</summary>
public sealed class IdempotencyExample : IExample
{
    /// <summary>带重试与幂等键的客户端：首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _client;

    public int Number => 35;

    public string Title => "幂等键：重试共用同一个键 / 显式键透传 / 默认关闭";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= new AxiosClient(new AxiosConfig
        {
            // POST 默认不重试（非幂等）；这里显式打开，正是幂等键存在的意义：同键重发不会被当成两次写。
            Retry =
            {
                Policy = new RetryPolicy
                {
                    MaxRetries = 3,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffFactor = 1d,
                    Jitter = false,
                    RetryNonIdempotentMethods = true
                }
            },
            Idempotency = { Enabled = true },
            Callbacks =
            {
                // 每次尝试读一次头：重试不会换键（写键是库的事，这里只观察）
                OnSendingRequest = (request, _, _) =>
                {
                    var key = request.Headers.TryGetValues("Idempotency-Key", out var values)
                        ? string.Join(",", values)
                        : "none";
                    Console.WriteLine($"    尝试发送：Idempotency-Key = {key}");
                    return default;
                }
            }
        });

        // 每次运行用新 tag：服务端按 tag 计「前 N 次失败」，结果稳定可复现
        var tag = Guid.NewGuid().ToString("N")[..8];

        // 1) 前两次 503、第三次成功：三次发送带的是同一个键
        var response = await client.PostAsync($"{context.BaseUrl}flaky?fail=2&tag={tag}-idem", new { orderId = 42 });
        Console.WriteLine($"  POST /flaky -> {response.Status}（重试没有换键，服务端只会认一次写）");

        // 2) 显式键：把上游系统的键原样透传，优先于 KeyFactory（默认客户端只在这一条请求上开幂等键）
        var explicitKey = "order-" + tag;
        var echo = await Axios.GetAsync(context.BaseUrl + "users/1", c =>
        {
            c.Idempotency.Enabled = true;
            c.Idempotency.Key = explicitKey;
        });
        Console.WriteLine($"  显式键 {explicitKey} -> {echo.Status}（Key 压过 KeyFactory 与默认 Guid）");

        Console.WriteLine("  说明: 默认关闭 —— 非幂等方法自动带键会把「重复写」变成「静默去重」");
    }
}
