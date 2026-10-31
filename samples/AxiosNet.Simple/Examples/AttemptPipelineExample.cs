using System.Diagnostics;
using AxiosNet;
using AxiosNet.Pipeline;
using AxiosNet.Retry;

namespace AxiosNet.Simple.Examples;

/// <summary>31. 尝试管线：一次尝试抽象成可组合中间件（重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 发送），可插、可重排。</summary>
public sealed class AttemptPipelineExample : IExample
{
    /// <summary>探针记录：每次运行开始清空，静态复用的客户端共用同一份。</summary>
    private static readonly List<string> ProbeLog = new();

    /// <summary>带探针的尝试管线：探针落在最内层（闸门之后、发送之前），静态复用避免按运行重建客户端。</summary>
    private static readonly AxiosAttemptPipeline Probed = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return await next(request, ctx);
        }
        finally
        {
            ProbeLog.Add($"第 {ctx.AttemptNumber} 次尝试 → {clock.ElapsedMilliseconds}ms");
        }
    });

    /// <summary>挂探针管线的重试客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _retrying;

    /// <summary>挂探针管线、不配重试的客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _single;

    public int Number => 31;

    public string Title => "尝试管线：默认链 / 插一个每次尝试的中间件 / 重排 / 与响应拦截器的分工";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var tag = Guid.NewGuid().ToString("N")[..8];

        // 1) 默认链：重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 发送。五个内置项永远在链上，
        //    是否真的干活由它们自己看本次请求的生效配置决定（没配策略 / 端点 / 分支数就是纯直通）。
        Console.WriteLine($"  默认链: {string.Join(" → ", AxiosAttemptPipeline.Default.Middleware.Select(Describe))} + 发送");

        // 2) 插一个「每次尝试」的探针：Append 落在最内层（闸门之后、发送之前），重试 N 次就调用 N+1 次。
        ProbeLog.Clear();

        var retrying = _retrying ??= new AxiosClient(
            new AxiosConfig { Retry = { Policy = new RetryPolicy(2, TimeSpan.FromMilliseconds(50)) } },
            pipeline: Probed);

        var response = await retrying.GetAsync($"{context.BaseUrl}flaky?fail=2&tag={tag}-probe");
        Console.WriteLine($"  重试 2 次后成功: status={response.Status}，探针记录 {ProbeLog.Count} 条（每次尝试一条）");
        foreach (var entry in ProbeLog)
        {
            Console.WriteLine($"    {entry}");
        }

        // 3) 没配重试时链上每一项都是纯直通：请求只走一遍，探针也就只记一条
        ProbeLog.Clear();
        var single = _single ??= new AxiosClient(pipeline: Probed);
        await single.GetAsync($"{context.BaseUrl}flaky?fail=0&tag={tag}-single");
        Console.WriteLine($"  不配重试: 探针只记录 {ProbeLog.Count} 条（{ProbeLog[0]}）");

        // 4) 重排与摘除：不可变，每次编辑返回新实例
        var noGate = AxiosAttemptPipeline.Default.Remove(Attempt.Gate);
        Console.WriteLine($"  摘掉闸门后: {string.Join(" → ", noGate.Middleware.Select(Describe))} + 发送");
        Console.WriteLine($"  默认链不受影响（不可变）: {AxiosAttemptPipeline.Default.Middleware.Count} 项");

        // 5) 插到最外层会被拒绝（ArgumentException）：该位置每次请求只调用一次，等价于响应拦截器。
        try
        {
            AxiosAttemptPipeline.Default.InsertBefore(Attempt.Retry, next => (request, ctx) => next(request, ctx));
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"  插到最外层被拒: {ex.Message[..Math.Min(48, ex.Message.Length)]}…");
        }

        // 6) 请求头在响应侧可读（响应拦截器 / 本示例都能用；短路响应没有请求消息，返回 null）
        var traced = await single.GetAsync($"{context.BaseUrl}flaky?fail=0&tag={tag}-header", c => c.Headers["X-Trace"] = tag);
        Console.WriteLine($"  响应侧读请求头: X-Trace={traced.GetRequestHeader("X-Trace")}（响应头用 GetHeader）");
    }

    private static string Describe(AxiosAttemptMiddleware middleware)
        => ReferenceEquals(middleware, Attempt.Retry) ? "重试"
            : ReferenceEquals(middleware, Attempt.Failover) ? "备用端点"
            : ReferenceEquals(middleware, Attempt.Circuit) ? "熔断"
            : ReferenceEquals(middleware, Attempt.Hedging) ? "对冲"
            : ReferenceEquals(middleware, Attempt.Gate) ? "闸门"
            : ReferenceEquals(middleware, Attempt.Audit) ? "审计"
            : "自定义";
}
