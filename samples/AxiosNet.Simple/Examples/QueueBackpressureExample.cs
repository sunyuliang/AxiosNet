using System.Diagnostics;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>33. 排队上限（背压）：在途名额 + 最大排队数，队列满立刻以 ERR_QUEUE_FULL 失败。</summary>
public sealed class QueueBackpressureExample : IExample
{
    /// <summary>2 个在途名额 + 1 个排队位：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _capped;

    /// <summary>对照用：不设排队上限，配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _unlimitedQueue;

    public int Number => 33;

    public string Title => "排队上限 / 背压：MaxConcurrentRequests + MaxQueueLength，队列满立刻失败";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 2 个在途名额 + 1 个排队位：同时来 4 条慢请求 —— 2 条在跑、1 条排队、1 条被拒
        var capped = _capped ??= new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            Limits = new()
            {
                MaxConcurrentRequests = 2,
                MaxQueueLength = 1
            }
        });

        Console.WriteLine("  闸门: MaxConcurrentRequests = 2, MaxQueueLength = 1，同时发起 4 条 400ms 的请求");

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(RunOneAsync));
        stopwatch.Stop();

        foreach (var line in results)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine($"  总耗时 {stopwatch.ElapsedMilliseconds}ms（两批 400ms 串行执行）");

        // 对照：不设 MaxQueueLength（null = 不限排队深度）时，同样 4 条都排进队列，全部成功
        var unlimitedQueue = _unlimitedQueue ??= new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            Limits = new() { MaxConcurrentRequests = 2 }
        });

        var relaxed = await Task.WhenAll(Enumerable.Range(1, 4).Select(async _ =>
        {
            var response = await unlimitedQueue.GetAsync<object>("slow?ms=200");
            return response.Status;
        }));

        Console.WriteLine($"  对照（MaxQueueLength = null）: 4 条全部成功，status = {string.Join(",", relaxed)}");
        Console.WriteLine("  说明: 被拒的请求没有发出去，也不回退到备用端点 —— 它是「入口太挤」，换端点解决不了；调用方应退避后重试");
        Console.WriteLine("  说明: MaxQueueLength 与 MaxConcurrentRequests 一起在首个受限请求时固化，构造之后改 Defaults 不会重建闸门");
        Console.WriteLine("  说明: 想让请求「不排队」用 QueueTimeout = TimeSpan.Zero（报 ERR_QUEUE_TIMEOUT）；排队等待计入 TotalTimeout，不计入 Timeout");

        async Task<string> RunOneAsync(int i)
        {
            try
            {
                var response = await capped.GetAsync<object>("slow?ms=400");
                return $"第 {i} 条: status={response.Status}（放行）";
            }
            catch (AxiosException ex)
            {
                return $"第 {i} 条: code={ex.Code}（没发出去 —— {ex.Message}）";
            }
        }
    }
}
