using System.Diagnostics;
using AxiosNet;
using AxiosNet.Circuit;

namespace AxiosNet.Simple.Examples;

/// <summary>28. 熔断：按 host 计数、连续失败打开、冷却后半开探测、单请求绕过、手工恢复。</summary>
public sealed class CircuitExample : IExample
{
    public int Number => 28;

    public string Title => "熔断：连续失败打开 / 半开探测 / 探测失败重开 / 单请求绕过 / 手工 Reset";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 阈值 / 冷却都取得很小，示例不必真的等 30 秒
        var breaker = new CircuitBreaker
        {
            FailureThreshold = 2,
            BreakDuration = TimeSpan.FromMilliseconds(500),
            HalfOpenAttempts = 1
        };

        // 打印状态迁移：Closed -> Open -> HalfOpen -> Open
        breaker.StateChanged += change =>
            Console.WriteLine($"    [StateChanged] {change.Host}: {change.Previous} -> {change.Current}");

        using var client = new AxiosClient(new AxiosConfig
        {
            Circuit =
            {
                Policy = breaker,
                OnRejected = ctx => Console.WriteLine($"    [OnRejected] {ctx.Host} 熔断中，请求没有发出")
            }
        });

        // ?fail=999：这个 tag 永远 503（演示「下游持续故障」）
        var tag = Guid.NewGuid().ToString("N")[..8];
        var uri = $"{context.BaseUrl}flaky?fail=999&tag={tag}-circuit";
        var host = new Uri(uri).Authority;

        // 1) 连续 2 次 503：失败计数到阈值，熔断打开
        for (var i = 1; i <= 2; i++)
        {
            try
            {
                await client.GetAsync(uri);
            }
            catch (AxiosException ex)
            {
                Console.WriteLine($"  第 {i} 次请求: code={ex.Code}, status={ex.Status}（真的发出去了）");
            }
        }

        // 2) 打开后：请求不再发出，直接以 ERR_CIRCUIT_OPEN 失败
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.GetAsync(uri);
        }
        catch (AxiosException ex)
        {
            stopwatch.Stop();
            Console.WriteLine(
                $"  熔断中: code={ex.Code}, 建议等待 {ex.RetryAfter?.TotalMilliseconds:0}ms"
                + $"（耗时 {stopwatch.ElapsedMilliseconds}ms，请求未发出）");
        }

        // 3) 冷却结束 → 半开：只放一个探测请求；服务端仍 503，探测失败 → 立刻重新打开
        await Task.Delay(600);
        try
        {
            await client.GetAsync(uri);
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  半开探测: code={ex.Code}, status={ex.Status}（探测失败，重新打开并重新计时）");
        }

        Console.WriteLine($"  当前状态: {breaker.GetState(host)}");

        // 4) 单请求绕过熔断：显式把策略置空（探针 / 健康检查用）
        try
        {
            await client.GetAsync(uri, c => c.Circuit.Policy = null);
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  单请求绕过: code={ex.Code}（这次照常发出去了）");
        }

        // 5) 运维手工恢复：清掉计数，下次请求重新从 0 开始
        breaker.Reset();
        Console.WriteLine($"  Reset 后状态: {breaker.GetState(host)}（失败计数已清零）");

        Console.WriteLine("  说明: 只对「下游故障」计数（网络错误 / 超时 / 5xx）；取消与 4xx 既不清零也不累加");
    }
}
