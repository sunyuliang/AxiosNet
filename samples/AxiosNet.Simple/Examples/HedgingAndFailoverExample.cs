using System.Diagnostics;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>32. 并发对冲与备用端点：首个成功者胜出、全失败交给重试、当前端点用不了就换端点。</summary>
public sealed class HedgingAndFailoverExample : IExample
{
    public int Number => 32;

    public string Title => "并发对冲 / 备用端点：首个成功结果胜出、全失败交给重试、换端点重发";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // ---- 1) 并发对冲：首发之后 100ms 再放一条同样的请求 ----
        using var hedged = new AxiosClient(new AxiosConfig
        {
            Hedging = { MaxHedges = 1, Delay = TimeSpan.FromMilliseconds(100) }
        });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        var response = await hedged.GetAsync($"{context.BaseUrl}flaky?fail=1&tag={tag}-hedge");
        stopwatch.Stop();

        Console.WriteLine(
            $"  对冲: status={response.Status}, data={response.Data}, 耗时 {stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine("  说明: 首发吃到 503，100ms 后的副本成功 —— 第一个成功结果胜出，败者被取消并收尾");

        // 对照：这次请求关掉对冲，只剩首发；503 交给重试，没配重试就抛给调用方
        try
        {
            await hedged.GetAsync(
                $"{context.BaseUrl}flaky?fail=1&tag={tag}-single",
                c => c.Hedging.MaxHedges = 0);
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  对照（单次发送）: code={ex.Code}, status={ex.Status}");
        }

        // ---- 2) 备用端点：主端点连不上时换到演示服务器的同路径 ----
        using var failover = new AxiosClient(new AxiosConfig
        {
            Failover = { Endpoints = new[] { new Uri(context.BaseUrl) } }
        });

        var mirrored = await failover.GetAsync("http://127.0.0.1:1/users/1");
        Console.WriteLine(
            $"  备用端点: status={mirrored.Status}, data={mirrored.Data}, 实际发出={mirrored.Request?.RequestUri}");

        Console.WriteLine("  说明: 只有网络错误 / 发出后的超时 / 连接池耗尽 / 熔断拒绝 / 5xx 才换端点；4xx、排队超时、取消、预算耗尽都没发出，都不换");
        Console.WriteLine("  说明: 每个端点各有自己的超时预算与熔断账；换端点后租约、熔断维度与遥测标签都跟着新端点走");
    }
}
