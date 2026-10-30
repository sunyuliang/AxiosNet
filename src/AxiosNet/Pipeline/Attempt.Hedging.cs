using System.Runtime.ExceptionServices;
using AxiosNet.Internal;
using AxiosNet.Retry;

namespace AxiosNet.Pipeline;

// 「一次尝试」的对冲：首发之后按 Delay 放出延迟副本，第一个成功结果胜出，其余分支取消并收尾。
public static partial class Attempt
{
    /// <summary>
    /// 对冲：首发之后按 <see cref="AxiosConfig.HedgingSettings.Delay"/> 的间隔再放出延迟副本，
    /// 第一个成功结果胜出；其余分支取消并等它们真正收尾。所有分支都失败时把失败原样抛给外层的重试。
    /// </summary>
    /// <remarks>
    /// 位置在 <see cref="Retry"/>、<see cref="Failover"/>、<see cref="Circuit"/> 之内、<see cref="Gate"/> 之外：
    /// 熔断看整组分支的最终结果，每条分支各过并发闸门、各占一个槽位 —— 开启对冲后占用的并发额度是
    /// <c>1 + MaxHedges</c> 倍，<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/> 要按此预留。
    /// <c>Hedging.MaxHedges = 0</c> 时纯直通。流式响应、非幂等方法、正文不能并发重放、响应侧带共享状态的请求
    /// 按单次发送处理。每条分支各执行一次 <c>transformResponse</c> 与响应侧回调（含落败分支）；
    /// 响应拦截器在管线之外，仍只对胜出结果执行一次。
    /// </remarks>
    public static AxiosAttemptMiddleware Hedging { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 不配对冲时纯直通。
        return (request, ctx) =>
            ctx.Hedging is { MaxHedges: > 0 } hedging && CanHedge(request, ctx, hedging)
                ? HedgeAsync(next, request, ctx, hedging)
                : next(request, ctx);
    };

    /// <summary>这次请求能不能对冲：不满足时按一次普通发送处理。</summary>
    private static bool CanHedge(
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        // 流式响应不对冲：败者的正文没人读。
        if (ctx.StreamResponse)
        {
            return false;
        }

        // 非幂等方法默认不对冲。
        if (!hedging.HedgeNonIdempotentMethods && !RetryPolicy.IsIdempotent(ctx.Config.Method))
        {
            return false;
        }

        // 响应侧带共享状态（下载进度回调 / 限速器）不对冲。
        if (ctx.Config.CallbacksOrNull?.OnDownloadProgress is not null || ctx.Config.LimitsOrNull?.MaxRate is > 0)
        {
            return false;
        }

        // 正文必须能并发重放：流 / multipart / 进度包装都带共享可变状态。
        return IsParallelSafeBody(request.Content);
    }

    /// <summary>正文是否可被两条分支同时序列化：只有内存缓冲形态才是安全的。</summary>
    private static bool IsParallelSafeBody(HttpContent? content)
        => content switch
        {
            null => true,

            // string / byte[] / form-urlencoded / JSON 文本都落在 ByteArrayContent 上。
            ByteArrayContent => true,

            // ReadOnlyMemory 的零拷贝正文。
            ReadOnlyMemoryContent => true,

            // StreamContent / MultipartContent / ProgressHttpContent / 调用方自备内容：一律按不安全处理。
            _ => false
        };

    /// <summary>
    /// 放出各条分支并等待第一个成功结果。失败分支的异常先记账；全部失败时抛第一条失败，
    /// 让外层重试决定是否再来一轮。
    /// </summary>
    private static async ValueTask<AxiosAttemptResult> HedgeAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        var branches = hedging.MaxHedges + 1;

        // 链接调用方令牌：胜出者出现时取消败者，调用方取消时连锁取消所有分支。
        using var group = CancellationTokenSource.CreateLinkedTokenSource(ctx.ExternalToken);

        var remaining = new List<Task<AxiosAttemptResult>>(branches);
        for (var index = 0; index < branches; index++)
        {
            remaining.Add(SendHedgeBranchAsync(next, template, ctx, hedging.Delay, index, group.Token));
        }

        Exception? firstFailure = null;
        try
        {
            while (remaining.Count > 0)
            {
                Task<AxiosAttemptResult> completed;
                try
                {
                    // 分支可以不响应取消令牌：等待期间也要跟着调用方令牌走，否则取消要等到某条分支自己返回才生效。
                    completed = await Task.WhenAny(remaining).WaitAsync(ctx.ExternalToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    throw AxiosException.Canceled(ctx.Config, template, "请求在对冲等待期间被取消", oce);
                }

                remaining.Remove(completed);

                try
                {
                    // 第一个成功结果胜出：谁先返回用谁。
                    // Ensure 挡住中间件返回的 default 结果。
                    var winner = AxiosAttemptResult.Ensure(await completed.ConfigureAwait(false), ctx.Config);

                    // 胜出者分支的配置回填到共享配置：调用方按契约从 ctx.Config.Response 读响应信息。
                    if (winner.Response.Config is { } branchConfig)
                    {
                        if (branchConfig.Response is { } responseInfo)
                        {
                            ctx.Config.Response = responseInfo;
                        }

                        // 交出去的响应指向本次请求的配置，而不是分支副本。
                        winner.Response.Config = ctx.Config;
                    }

                    return winner;
                }
                catch (Exception ex) when (IsCleanupCancellation(ex, ctx))
                {
                    // 败者被链接令牌取消：清理，不算失败。
                }
                catch (Exception ex)
                {
                    // 闸门拒绝会盖掉真正的下游故障：已记下它时用后到的真实失败替换。
                    if (firstFailure is null || IsGateRejection(firstFailure))
                    {
                        firstFailure = ex;
                    }
                }
            }

            // 全部分支都失败：交给外层的重试。
            if (firstFailure is null)
            {
                throw AxiosException.Canceled(ctx.Config, template, "所有对冲分支都被取消");
            }

            ExceptionDispatchInfo.Capture(firstFailure).Throw();
            throw new InvalidOperationException("对冲分支全部失败但没有可抛出的异常。");
        }
        finally
        {
            // 取消仍在路上的分支并等它们收尾：否则会继续占着连接租约与并发槽位。
            group.Cancel();
            await DiscardPendingAsync(remaining, ctx.ExternalToken.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    /// <summary>胜出后等待败者收尾的整组上限：不响应取消令牌的分支最多挡胜出者这么久，之后改由后台续延收尾。</summary>
    private static readonly TimeSpan DiscardGrace = TimeSpan.FromSeconds(2);

    /// <summary>收尾所有没被采用的分支：整组共用一个截止时刻，而不是每条各等一份上限。</summary>
    /// <param name="pending">还没被观察过的分支。</param>
    /// <param name="callerCanceled">调用方已取消：收尾不占调用方的等待时间，全部交给后台续延。</param>
    private static async Task DiscardPendingAsync(List<Task<AxiosAttemptResult>> pending, bool callerCanceled)
    {
        if (pending.Count == 0)
        {
            return;
        }

        if (callerCanceled)
        {
            // 取消语义是「尽快退出」：不响应的分支不能把调用方拖到收尾上限。
            foreach (var branch in pending)
            {
                _ = DrainAsync(branch);
            }

            return;
        }

        using var deadline = new CancellationTokenSource(DiscardGrace);

        foreach (var branch in pending)
        {
            try
            {
                Discard(await branch.WaitAsync(deadline.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // 要么分支不响应取消（还没结束），要么与截止时刻撞车：交给后台续延收尾。
                _ = DrainAsync(branch);
            }
            catch
            {
                // 失败 / 被取消的分支：终端已经在自己的失败路径上归还了租约与槽位。
            }
        }
    }

    /// <summary>后台等分支真正结束并释放它持有的库内资源：结果已经丢弃，不能让收尾反过来挡住调用方。</summary>
    private static async Task DrainAsync(Task<AxiosAttemptResult> branch)
    {
        try
        {
            Discard(await branch.ConfigureAwait(false));
        }
        catch
        {
            // 这里没有观察者：失败 / 取消只需吞掉。
        }
    }

    /// <summary>这条失败是不是「并发闸门拒绝」（排队超时 / 队列满）：它说明不了下游健康与否。</summary>
    private static bool IsGateRejection(Exception exception)
        => exception is AxiosException { Code: AxiosException.ErrQueueTimeout or AxiosException.ErrQueueFull };

    /// <summary>
    /// 释放一条败者结果持有的库内资源（流式凭证：正文流 + 连接租约 + 并发槽位）。
    /// 非流式正文归调用方 / 序列化器所有，不在这里释放；流式正文也不排空。
    /// </summary>
    private static void Discard(AxiosAttemptResult result)
        => result.StreamOwner?.Dispose();

    /// <summary>
    /// 第 <paramref name="index"/> 条分支：先按 <c>index × delay</c> 等待，再换一条自己的请求消息发送。
    /// 第 0 条直接用模板；其余按模板复制（正文沿用同一份，已由 <see cref="IsParallelSafeBody"/> 保证可并发重放）。
    /// </summary>
    private static async Task<AxiosAttemptResult> SendHedgeBranchAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        TimeSpan delay,
        int index,
        CancellationToken branchToken)
    {
        if (index > 0 && delay > TimeSpan.Zero)
        {
            // 起跑时刻是 n × delay，按计时器上限收敛。
            var wait = TimeSpan.FromTicks(Math.Min(delay.Ticks * index, Timeouts.Max.Ticks));

            try
            {
                await Task.Delay(wait, branchToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                if (ctx.ExternalToken.IsCancellationRequested)
                {
                    // 调用方在等待期间取消：按统一错误模型报「已取消」。
                    throw AxiosException.Canceled(ctx.Config, template, "请求在对冲分支起跑前被取消", oce);
                }

                // 胜出者已经出现：这条分支不必起跑。
                throw;
            }
        }

        var request = index == 0 ? template : CloneRequest(template);
        try
        {
            // 每条分支用自己的令牌与配置副本：否则败者被取消时会中止胜者，两条分支也会互相覆盖响应。
            var branchContext = ctx
                .WithoutTarget()
                .WithConfig(ctx.Config.CloneForAttempt())
                .WithAttemptToken(branchToken);
            return await next(request, branchContext).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(request, template))
            {
                // 正文共享：先摘下正文，只释放这条请求。
                request.Content = null;
                request.Dispose();
            }
        }
    }
}
