using System.Runtime.ExceptionServices;
using AxiosNet.Retry;

namespace AxiosNet.Pipeline;

// 「一次尝试」的对冲：首发之后按 Delay 放出延迟副本，第一个成功结果胜出，其余分支取消并收尾。
public static partial class Attempt
{
    /// <summary>对冲：首发之后按 <see cref="AxiosConfig.HedgingSettings.Delay"/> 放出延迟副本，第一个成功结果胜出，其余分支取消并收尾。</summary>
    /// <remarks>
    /// 位置在 <see cref="Retry"/>、<see cref="Failover"/>、<see cref="Circuit"/> 之内、<see cref="Gate"/> 之外：
    /// 熔断看整组分支的最终结果，每条分支各占一个并发槽位。
    /// <c>MaxHedges = 0</c> 时纯直通；全部失败时把第一条失败抛给外层重试。流式响应、非幂等方法、正文不能并发重放、
    /// 带进度回调或限速的请求按单次发送。响应拦截器只对胜出结果执行一次。
    /// </remarks>
    public static AxiosAttemptMiddleware Hedging { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            ctx.Hedging is { MaxHedges: > 0 } hedging && CanHedge(request, ctx, hedging)
                ? HedgeAsync(next, request, ctx, hedging)
                : next(request, ctx);
    };

    /// <summary>这次请求能否对冲：不满足时按一次普通发送处理。</summary>
    private static bool CanHedge(
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        // 流式响应不对冲。
        if (ctx.StreamResponse)
        {
            return false;
        }

        // 非幂等方法默认不对冲，除非 AllowUnsafeRetry。
        if (!ctx.Config.AllowUnsafeRetry && !RetryPolicy.IsIdempotent(ctx.Config.Method))
        {
            return false;
        }

        // 进度回调与限速器带共享状态，分支并发会丢字节或重复上报。
        var observability = ctx.Config.ObservabilityOrNull;
        if (observability?.OnUploadProgress is not null
            || observability?.OnDownloadProgress is not null
            || ctx.Config.TransportOrNull?.MaxRate is > 0)
        {
            return false;
        }

        // 正文必须能并发重放。
        return BodyReplayability.CanReplay(ctx.Config, ReplayMode.Hedge);
    }

    /// <summary>放出各条分支并等待第一个成功结果；全部失败时抛第一条失败，交给外层重试。</summary>
    private static async ValueTask<AxiosAttemptResult> HedgeAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        var branches = hedging.MaxHedges + 1;

        // 链接调用方令牌：胜出或调用方取消时连锁取消各分支。
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
                    // 等待期间也跟随调用方令牌。
                    completed = await Task.WhenAny(remaining).WaitAsync(ctx.ExternalToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    throw AxiosException.Canceled(ctx.Config, template, "请求在对冲等待期间被取消", oce);
                }

                remaining.Remove(completed);

                try
                {
                    // Ensure 挡住中间件返回的 default 结果。
                    var winner = AxiosAttemptResult.Ensure(await completed.ConfigureAwait(false), ctx.Config);

                    // 胜出者的配置回填到共享配置。
                    if (winner.Response.Config is { } branchConfig)
                    {
                        if (branchConfig.Response is { } responseInfo)
                        {
                            ctx.Config.Response = responseInfo;
                        }

                        winner.Response.Config = ctx.Config;
                    }

                    return winner;
                }
                catch (Exception ex) when (IsCleanupCancellation(ex, ctx))
                {
                    // 败者被链接令牌取消：清理，不算失败。
                }
                catch (Exception ex) when (IsBudgetExhausted(ex))
                {
                    // 分支共享同一总预算：任一分支到点即全部到点。
                    if (ex is AxiosException { IsTotalTimeoutExceeded: true })
                    {
                        throw;
                    }

                    throw AsBudgetExhausted(ex, ctx, template);
                }
                catch (Exception ex)
                {
                    // 闸门拒绝不代表下游故障；已有真实失败时不覆盖。
                    if (firstFailure is null || IsGateRejection(firstFailure))
                    {
                        firstFailure = ex;
                    }
                }
            }

            // 全部分支都失败：交给外层重试。
            if (firstFailure is null)
            {
                throw AxiosException.Canceled(ctx.Config, template, "所有对冲分支都被取消");
            }

            ExceptionDispatchInfo.Capture(firstFailure).Throw();
            throw new InvalidOperationException("对冲分支全部失败但没有可抛出的异常。");
        }
        finally
        {
            // 取消仍在路上的分支并等它们收尾。
            group.Cancel();
            await DiscardPendingAsync(remaining, ctx.ExternalToken.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    /// <summary>胜出后等待败者收尾的整组上限，超时改由后台续延收尾。</summary>
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
            // 取消语义是尽快退出，不占调用方的等待时间。
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
                // 分支尚未结束或与截止时刻撞车，交给后台续延收尾。
                _ = DrainAsync(branch);
            }
            catch
            {
                // 失败分支：终端已归还租约与槽位。
            }
        }
    }

    /// <summary>后台等分支结束并释放库内资源。</summary>
    private static async Task DrainAsync(Task<AxiosAttemptResult> branch)
    {
        try
        {
            Discard(await branch.ConfigureAwait(false));
        }
        catch
        {
            // 没有观察者，吞掉失败与取消。
        }
    }

    /// <summary>是否为并发闸门拒绝（排队超时 / 队列满）。</summary>
    private static bool IsGateRejection(Exception exception)
        => exception is AxiosException { Code: AxiosException.ErrQueueTimeout or AxiosException.ErrQueueFull };

    /// <summary>释放败者结果持有的库内资源（正文流 + 连接租约 + 并发槽位）；非流式正文归调用方所有，流式正文不排空。</summary>
    private static void Discard(AxiosAttemptResult result)
        => result.StreamOwner?.Dispose();

    /// <summary>第 <paramref name="index"/> 条分支：先按 <c>index × delay</c> 等待，再换一条自己的请求消息发送（正文沿用同一份）。</summary>
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
                    // 调用方取消：报「已取消」。
                    throw AxiosException.Canceled(ctx.Config, template, "请求在对冲分支起跑前被取消", oce);
                }

                // 胜出者已出现，不必起跑。
                throw;
            }
        }

        var request = index == 0 ? template : CloneRequest(template);
        try
        {
            // 每条分支用自己的令牌与配置副本。
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
