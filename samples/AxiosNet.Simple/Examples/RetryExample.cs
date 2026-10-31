using System.Diagnostics;
using AxiosNet;
using AxiosNet.Retry;

namespace AxiosNet.Simple.Examples;

/// <summary>25. 弹性重试：默认不重试、内置策略、只对幂等方法生效、OnRetry 观察、自定义策略。</summary>
public sealed class RetryExample : IExample
{
    /// <summary>带重试策略的客户端：策略挂在实例默认值上，一次配置处处生效。</summary>
    private static AxiosClient? _retrying;

    /// <summary>验证 POST 默认不重试的客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _post;

    /// <summary>自定义重试策略客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _custom;

    /// <summary>正文可重放演示客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _replay;

    public int Number => 25;

    public string Title => "弹性重试：默认不重试 / 次数与间隔 / 幂等判定 / OnRetry / 自定义策略";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

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

        // 1) 默认不重试：Axios 静态门面的默认客户端没有任何重试策略，503 直接抛给调用方，一次请求就结束
        var plainUri = $"{context.BaseUrl}flaky?fail=999&tag={tag}-plain";
        try
        {
            await Axios.GetAsync(plainUri);
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
        var postClient = _post ??= new AxiosClient(new AxiosConfig
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
        var customClient = _custom ??= new AxiosClient(new AxiosConfig { Retry = { Policy = new StatusCodePolicy(503, 2) } });
        var custom = await customClient.GetAsync($"{context.BaseUrl}flaky?fail=1&tag={tag}-custom");
        Console.WriteLine($"  自定义策略（只重试 503）: status={custom.Status}");

        // 5) 正文可重放：调用方自带的 HttpContent 按白名单放行；其余自定义内容在发送前就拒绝，
        //    而不是等到第二次发送才发现「流已被消费、只发出空正文」。
        var replayClient = _replay ??= new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(1, TimeSpan.Zero) }
        });

        var replayable = await replayClient.PostAsync($"{context.BaseUrl}echo", new StringContent("hello"));
        Console.WriteLine($"  白名单内（StringContent）: status={replayable.Status}");

        try
        {
            await replayClient.PostAsync($"{context.BaseUrl}echo", new OneShotContent());
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  自定义 HttpContent: code={ex.Code}（发送前拒绝，避免重发时只发出空正文）");
        }

        Console.WriteLine("  说明: Timeout 是「每次尝试」的预算；正文只序列化一次并原样重发，进度跨次累计");
    }

    /// <summary>一次性正文：库认不出可重放形态，重试 / 换端点开启时会在发送前拒绝。</summary>
    private sealed class OneShotContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => await stream.WriteAsync("hello"u8.ToArray()).ConfigureAwait(false);

        protected override bool TryComputeLength(out long length)
        {
            length = 5;
            return true;
        }
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
