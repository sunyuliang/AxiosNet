using AxiosNet;
using AxiosNet.Retry;

namespace AxiosNet.Simple.Examples;

/// <summary>34. 全局重试预算：RetryBudgetPolicy 用共享令牌桶限制整个实例的重试总量，下游故障时不放大重试。</summary>
public sealed class RetryBudgetExample : IExample
{
    public int Number => 34;

    public string Title => "全局重试预算：RetryBudgetPolicy 限制整个实例的重试总量";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 下游一直 503（每个 tag 前 99 次都失败）：每条请求都要把重试用满才返回
        const string failing = "flaky?fail=99";

        // 对照：只配重试 —— 每条请求各自重试，重试量跟着请求数线性放大
        using var unbounded = new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(20)) }
        });

        var baselineRetries = await SendThreeAsync(unbounded, failing);
        Console.WriteLine($"  不带预算: 3 条请求共 {3 + baselineRetries} 次发送（每条 1 首发 + 最多 2 重试），下游故障时重试跟着请求数放大");

        // 预算：瞬时放行 2 次重试、不补充 —— 前 1 条请求用光，后面两条失败即返回
        var budget = new RetryBudgetPolicy(
            RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(20)),
            capacity: 2,
            refillPerSecond: 0);

        budget.OnBudgetExhausted = ctx =>
            Console.WriteLine($"    [OnBudgetExhausted] 第 {ctx.Attempt} 次尝试后预算不足，放弃重试（{ctx.ErrorCode}）");

        using var throttled = new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            Retry = { Policy = budget }
        });

        var throttledRetries = await SendThreeAsync(throttled, failing);
        Console.WriteLine($"  带预算:   3 条请求共 {3 + throttledRetries} 次发送，被拒重试 {budget.RefusedRetries} 次");
        Console.WriteLine("  说明: 预算按策略对象共享 —— 挂在 Defaults.Retry.Policy 上就是整个实例（含声明式客户端）共用一只桶");
        Console.WriteLine("  说明: 只拦重试不拦首发；放弃重试时抛的是真实失败（这里仍是 ERR_BAD_RESPONSE），不是新的错误码");
        Console.WriteLine("  说明: 需要「持续允许一定速率」就设 refillPerSecond（例如每秒 5 次）；0 表示用完就停");

        async Task<int> SendThreeAsync(AxiosClient client, string target)
        {
            var retries = 0;
            client.Defaults.Retry.OnRetry = _ => Interlocked.Increment(ref retries);

            for (var i = 1; i <= 3; i++)
            {
                // tag 每次唯一：演示服务器按 tag 计数，避免上一次运行留下的失败次数影响本次
                try
                {
                    await client.GetAsync<object>($"{target}&tag={Guid.NewGuid():N}");
                }
                catch (AxiosException ex)
                {
                    Console.WriteLine($"    第 {i} 条: {ex.Code}（status={ex.Status}）");
                }
            }

            return retries;
        }
    }
}
