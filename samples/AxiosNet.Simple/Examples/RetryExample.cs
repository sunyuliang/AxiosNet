using System.Diagnostics;
using AxiosNet;
using AxiosNet.Retry;

namespace AxiosNet.Simple.Examples;

/// <summary>25. 弹性重试：默认不重试、内置策略、只对幂等方法生效、OnRetry 观察、自定义策略。</summary>
public sealed class RetryExample : IExample
{
    /// <summary>演示服务器：首次运行创建后跨运行复用（服务端的 /flaky 计数会随运行累计）。</summary>
    private static AxiosClient? _plain;

    /// <summary>带重试策略的客户端：策略挂在实例默认值上，一次配置处处生效。</summary>
    private static AxiosClient? _retrying;

    public int Number => 25;

    public string Title => "弹性重试：默认不重试 / 次数与间隔 / 幂等判定 / OnRetry / 自定义策略";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var plain = _plain ??= new AxiosClient();
        var retrying = _retrying ??= new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                // 内置策略：最多重试 3 次，200ms 起每次翻倍（200/400/800ms），带抖动
                Policy = new RetryPolicy { MaxRetries = 3, Delay = TimeSpan.FromMilliseconds(200) },
                OnRetry = ctx => Console.WriteLine(
                    $"    [OnRetry] 第 {ctx.Attempt} 次尝试失败（{ctx.ErrorCode}），"
                    + $"{ctx.Delay.TotalMilliseconds:0}ms 后重试（最多重试 {ctx.MaxRetries} 次）")
            }
        });

        // 每次运行用新 tag：服务端按 tag 计「前 N 次失败」，结果稳定可复现
        var tag = Guid.NewGuid().ToString("N")[..8];

        // 1) 默认不重试：503 直接抛给调用方，一次请求就结束
        var plainUri = $"{context.BaseUrl}flaky?fail=999&tag={tag}-plain";
        try
        {
            await plain.GetAsync(plainUri);
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  默认不重试: code={ex.Code}, status={ex.Status}（只发了一次请求）");
        }

        // 2) 挂上策略：同一个端点，前几次 503 会自动重试，直到成功
        var flakyUri = $"{context.BaseUrl}flaky?fail=2&tag={tag}-retry";
        var stopwatch = Stopwatch.StartNew();
        var response = await retrying.GetAsync(flakyUri);
        stopwatch.Stop();

        Console.WriteLine($"  重试后成功: status={response.Status}, data={response.Data}（耗时 {stopwatch.ElapsedMilliseconds}ms）");

        // 3) 幂等判定：POST 默认不重试（非幂等），即使挂了策略也只发一次
        using var postClient = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(100)) }
        });

        try
        {
            await postClient.PostAsync($"{context.BaseUrl}flaky?fail=999&tag={tag}-post", new { orderId = 42 });
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  POST 默认不重试: code={ex.Code}（服务端可能已执行，重发会产生重复副作用）");
        }

        // 4) 完全自定义：实现 IAxiosRetryPolicy（这里只对 503 重试，且固定等 50ms）
        using var customClient = new AxiosClient(new AxiosConfig { Retry = { Policy = new StatusCodePolicy(503, 2) } });
        var custom = await customClient.GetAsync($"{context.BaseUrl}flaky?fail=1&tag={tag}-custom");
        Console.WriteLine($"  自定义策略（只重试 503）: status={custom.Status}");

        Console.WriteLine("  说明: Timeout 是「每次尝试」的预算；正文只序列化一次并原样重发，进度跨次累计");
    }

    /// <summary>只对指定状态码重试的自定义策略（演示 <see cref="IAxiosRetryPolicy"/> 的接入方式）。</summary>
    private sealed class StatusCodePolicy : IAxiosRetryPolicy
    {
        private readonly int _status;
        private readonly TimeSpan _delay;

        public StatusCodePolicy(int status, int maxRetries, TimeSpan? delay = null)
        {
            _status = status;
            MaxRetries = maxRetries;
            _delay = delay ?? TimeSpan.FromMilliseconds(50);
        }

        public int MaxRetries { get; }

        public bool ShouldRetry(AxiosRetryContext context) => context.StatusCode == _status;

        public TimeSpan GetDelay(AxiosRetryContext context) => _delay;
    }
}
