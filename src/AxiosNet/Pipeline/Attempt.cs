using System.Runtime.ExceptionServices;
using AxiosNet.Circuit;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

/// <summary>
/// 内置尝试中间件的具名入口：<see cref="Retry"/> → <see cref="Failover"/> → <see cref="Circuit"/> →
/// <see cref="Hedging"/> → <see cref="Gate"/> → <see cref="Audit"/>（顺序即默认链序）。
/// </summary>
/// <remarks>
/// 六项恒在默认链上，摘掉需显式 <see cref="AxiosAttemptPipeline.Remove"/> 或整体重建；
/// 是否生效由本次请求的生效配置决定（未配置策略 / 端点 / 分支数时交给下一环）。
/// </remarks>
public static partial class Attempt
{
    /// <summary>
    /// 重试：最外层的循环型中间件。每次尝试换一条新请求（正文与请求头沿用模板）；
    /// 未配策略或 <c>MaxRetries &lt;= 0</c> 时直接交给下一环。
    /// </summary>
    public static AxiosAttemptMiddleware Retry { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            ctx.Retry?.Policy is { } policy && policy.MaxRetries > 0
                ? RetryLoopAsync(next, request, ctx, policy)
                : next(request, ctx);
    };

    /// <summary>
    /// 熔断：每次尝试之前的准入闸门；拒绝时以 <c>ERR_CIRCUIT_OPEN</c> 失败，请求不会发出。
    /// 未配策略时直接交给下一环。
    /// </summary>
    public static AxiosAttemptMiddleware Circuit { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            ctx.Circuit?.Policy is { } policy
                ? WithCircuitAsync(next, request, ctx, policy)
                : next(request, ctx);
    };

    /// <summary>
    /// 并发闸门：占用本次尝试的槽位。排队时间不计入 <see cref="AxiosConfig.Timeout"/>，
    /// 但计入 <see cref="AxiosConfig.ResilienceSettings.TotalTimeout"/>（更紧时按剩余预算截断等待）。
    /// 未设并发上限时直接交给下一环。
    /// </summary>
    public static AxiosAttemptMiddleware Gate { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            ctx.Gate is { } gate
                ? WithSlotAsync(next, request, ctx, gate)
                : next(request, ctx);
    };

    /// <summary>
    /// 重试循环：每次尝试有独立的超时预算（<see cref="AxiosConfig.Timeout"/> 为单次尝试上限），
    /// 调用方令牌贯穿所有尝试与等待。
    /// </summary>
    private static async ValueTask<AxiosAttemptResult> RetryLoopAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        IAxiosRetryPolicy policy)
    {
        var retry = ctx.Retry!;
        var externalToken = ctx.ExternalToken;

        // 校验正文可重放。
        var bodyRewind = RetryBody.Prepare(ctx.Config);

        // 第一条真实失败（非闸门拒绝）。
        Exception? firstRealFailure = null;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // 拨回正文读指针。
                bodyRewind?.Rewind(ctx.Config);
            }

            // 首次尝试用模板，后续尝试按模板复制。
            var request = attempt == 1 ? template : CloneRequest(template);
            try
            {
                return await next(request, ctx.WithAttemptNumber(attempt)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消类异常不参与重试判定，直接冒到调用方。
                var failed = new AxiosRetryContext(attempt, policy.MaxRetries, ex, ctx.Config, (ex as AxiosException)?.Response);

                // 总预算耗尽：原样抛出。
                if (ex is AxiosException { IsTotalTimeoutExceeded: true })
                {
                    throw;
                }

                // 未拿到槽位（排队超时 / 队列满）：请求未发出，库级不重试；已有真实失败时优先抛那一条。
                if (IsGateRejection(ex))
                {
                    if (firstRealFailure is not null)
                    {
                        ExceptionDispatchInfo.Capture(firstRealFailure).Throw();
                    }
                }
                else
                {
                    // 只记第一条。
                    firstRealFailure ??= ex;
                }

                // 库级终止原因先判：次数到顶 / 取消 / 熔断 / 闸门 / 配置错误。
                if (!IsRetryableFailure(failed, externalToken, policy))
                {
                    throw;
                }

                // 剩余预算已用尽：下一次尝试发不出去。
                if (ex is AxiosException { TimedOutOnTotalBudget: true })
                {
                    throw AxiosException.TotalTimeoutExceeded(ctx.Config, request, attempt + 1, ctx.Config.TotalTimeoutValue, ex);
                }

                // 询问策略是否重试；返回 false 时原样抛给调用方。
                if (!policy.ShouldRetry(failed))
                {
                    throw;
                }

                // 重试未发出时退回令牌。
                var retryScheduled = false;
                try
                {
                    if (TotalTimeoutBudget.IsExhausted(ctx.RemainingTotalTimeout))
                    {
                        throw AxiosException.TotalTimeoutExceeded(ctx.Config, request, attempt + 1, ctx.Config.TotalTimeoutValue, ex);
                    }

                    var delay = policy.GetDelay(failed);
                    if (delay < TimeSpan.Zero)
                    {
                        // 负值按不等待处理。
                        delay = TimeSpan.Zero;
                    }
                    else if (delay > Timeouts.Max)
                    {
                        // 收敛到 Task.Delay 上限。
                        delay = Timeouts.Max;
                    }

                    // 退避后即到截止点：不再发起下一次尝试。
                    if (ctx.RemainingTotalTimeout is { } remainingBudget
                        && remainingBudget - delay <= TotalTimeoutBudget.TimerResolution)
                    {
                        throw AxiosException.TotalTimeoutExceeded(ctx.Config, request, attempt + 1, ctx.Config.TotalTimeoutValue, ex);
                    }

                    // 回调与遥测共用同一个上下文
                    var retryContext = new AxiosRetryContext(
                        attempt, policy.MaxRetries, ex, ctx.Config, failed.Response, delay);

                    // 回调异常原样抛出，不再询问策略。
                    retry.OnRetry?.Invoke(retryContext);

                    // 与 OnRetry 同点派发。
                    AxiosTelemetry.Dispatch.RequestRetried(retryContext, ctx.Uri);

                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, externalToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException oce)
                        {
                            // 等待被取消：归一为已取消错误。
                            throw AxiosException.Canceled(ctx.Config, request, "请求在重试等待期间被取消", oce);
                        }
                    }

                    retryScheduled = true;
                }
                finally
                {
                    if (!retryScheduled)
                    {
                        RefundRetryBudget(policy);
                    }
                }
            }
            finally
            {
                if (!ReferenceEquals(request, template))
                {
                    // 正文是共享的，只释放请求本身。
                    request.Content = null;
                    request.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 带熔断的单次尝试：放行 → 发送 → 回报。拒绝时抛 <c>ERR_CIRCUIT_OPEN</c>；
    /// 放行后回报恰好一次（含取消与 4xx）。
    /// </summary>
    private static async ValueTask<AxiosAttemptResult> WithCircuitAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        IAxiosCircuitPolicy policy)
    {
        // 调用方已取消：先报取消。
        if (ctx.ExternalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(ctx.Config, request);
        }

        var context = new AxiosCircuitContext(CircuitHost(ctx.Uri), ctx.Uri, ctx.Config);

        // 内置熔断器额外返回准入票与建议等待时间。
        long ticket = 0;
        TimeSpan retryAfter;
        bool allowed;
        if (policy is CircuitBreaker breaker)
        {
            allowed = breaker.ShouldAllow(context, out ticket, out retryAfter);
        }
        else
        {
            allowed = policy.ShouldAllow(context);
            retryAfter = allowed ? TimeSpan.Zero : policy.GetRetryAfter(context);
        }

        if (!allowed)
        {
            // 回调先于抛错执行，其异常原样抛出；上下文带上建议等待时间。
            ctx.Circuit!.OnRejected?.Invoke(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, retryAfter: retryAfter));

            throw AxiosException.CircuitOpen(ctx.Config, request, context.Host, retryAfter);
        }

        AxiosAttemptResult result;
        try
        {
            result = await next(request, ctx).ConfigureAwait(false);

            // 中间件未返回结果时补一个可定位的错误；该错误同样要回报。
            result = AxiosAttemptResult.Ensure(result, ctx.Config);
        }
        catch (Exception ex)
        {
            // 失败结论带上异常，策略据此区分下游故障与取消 / 4xx；策略异常原样抛出。
            RecordCircuit(policy, new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, ex), ticket);
            throw;
        }

        // 成功路径回报状态码（<c>validateStatus</c> 放行时也可能是 5xx）。
        try
        {
            RecordCircuit(policy, new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, statusCode: result.Response.Status), ticket);
        }
        catch
        {
            // 策略异常抛出前释放流式正文租约。
            result.StreamOwner?.Dispose();
            throw;
        }

        return result;
    }

    /// <summary>
    /// 并发槽位：占到的槽位随上下文交给终端，由终端归还（失败路径）或随正文转交（流式成功路径）。
    /// 排队使用本次尝试的令牌。
    /// </summary>
    /// <remarks>
    /// 闸门自己兜底归还（内层中间件可以不调用 <c>next</c>）；槽位 <see cref="IDisposable.Dispose"/> 幂等；
    /// 租约经 <see cref="AxiosAttemptContext.WithLease"/> 追加，不覆盖内层。
    /// </remarks>
    private static async ValueTask<AxiosAttemptResult> WithSlotAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        ConcurrencyGate gate)
    {
        var slot = await gate.AcquireAsync(
            ctx.Config,
            request,
            ctx.AttemptToken,
            ctx.Config.ConcurrencyQueueTimeout,

            // 整条请求的剩余预算，比排队上限更紧时封顶。
            ctx.RemainingTotalTimeout,
            ctx.AttemptNumber).ConfigureAwait(false);

        AxiosAttemptResult result;
        try
        {
            result = await next(request, ctx.WithLease(slot)).ConfigureAwait(false);
        }
        catch
        {
            // 失败路径兜底归还（幂等）。
            slot.Dispose();
            throw;
        }

        // 成功路径：租约已转交正文时不归还。
        if (!result.LeasesTransferred)
        {
            slot.Dispose();
        }

        return result;
    }

    /// <summary>按模板复制一条可发送的请求（请求头逐个搬运，正文沿用同一份）；<paramref name="uri"/> 非 <c>null</c> 时替换目标地址。</summary>
    /// <param name="template">请求模板（模板本身不能再发）。</param>
    /// <param name="uri">替换后的目标地址；<c>null</c> 表示沿用模板地址。</param>
    internal static HttpRequestMessage CloneRequest(HttpRequestMessage template, Uri? uri = null)
    {
        var request = new HttpRequestMessage(template.Method, uri ?? template.RequestUri)
        {
            Version = template.Version,
#if !NETSTANDARD2_0
            VersionPolicy = template.VersionPolicy
#endif
        };

        // 换目标地址时不搬 Host。
        var retargeted = uri is not null;

        foreach (var header in template.Headers)
        {
            if (retargeted && string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // 搬运 Options / Properties 上的选项（索引器为显式实现，走字典接口）。
#if NETSTANDARD2_0
        var options = request.Properties;
        var sourceOptions = template.Properties;
#else
        var options = (IDictionary<string, object?>)request.Options;
        var sourceOptions = template.Options;
#endif
        foreach (var option in sourceOptions)
        {
            options[option.Key] = option.Value;
        }

        request.Content = template.Content;
        return request;
    }

    /// <summary>这次失败是否「被内部收掉」而非真失败：调用方未取消，但本次尝试的令牌已取消。</summary>
    /// <remarks>库内只有对冲会这样取消分支；这类取消不计失败、不发 <c>AttemptFailed</c>。两种取消形态都认。</remarks>
    internal static bool IsCleanupCancellation(Exception exception, in AxiosAttemptContext ctx)
        => !ctx.ExternalToken.IsCancellationRequested
           && FailureClassifier.IsCancellation(exception);

    /// <summary>
    /// 这次失败是否「整条请求的总预算已花完」：<c>IsTotalTimeoutExceeded</c> 表示下一次开始前即耗尽，
    /// <c>TimedOutOnTotalBudget</c> 表示本次尝试的上限被剩余预算压小后到点。
    /// </summary>
    internal static bool IsBudgetExhausted(Exception exception)
        => exception is AxiosException axios
           && (axios.IsTotalTimeoutExceeded || axios.TimedOutOnTotalBudget);

    /// <summary>把「预算已花完」的失败改报成对外统一口径。</summary>
    private static Exception AsBudgetExhausted(Exception failure, AxiosAttemptContext ctx, HttpRequestMessage request)
        => failure is AxiosException { TimedOutOnTotalBudget: true }
            ? AxiosException.TotalTimeoutExceededDuringAttempt(
                ctx.Config,
                request,
                ctx.AttemptNumber,
                ctx.Config.TotalTimeoutValue,
                failure)
            : failure;

    /// <summary>库级别的「不该重试」：次数到顶、调用方已取消、错误即取消、被熔断 / 闸门拦下、配置错误。</summary>
    /// <remarks>只做库内判定，<b>不调用</b> <see cref="IAxiosRetryPolicy.ShouldRetry"/>（策略可能带副作用）。</remarks>
    private static bool IsRetryableFailure(AxiosRetryContext context, CancellationToken externalToken, IAxiosRetryPolicy policy)
    {
        if (context.Attempt > policy.MaxRetries)
        {
            return false;
        }

        if (externalToken.IsCancellationRequested)
        {
            return false;
        }

        if (FailureClassifier.IsCancellation(context.Exception))
        {
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCircuitOpen })
        {
            // 熔断拒绝：请求未发出。
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrQueueTimeout or AxiosException.ErrQueueFull })
        {
            // 排队超时 / 队列满：请求未发出。
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrBadOptionValue })
        {
            // 配置错误：重发不改变结果。
            return false;
        }

        return true;
    }

    /// <summary>熔断的计数维度：<c>host:port</c>（同一 host 的不同端口是两个独立下游），非分层 URI 退化为 host。</summary>
    private static string CircuitHost(Uri uri)
        => uri.Authority is { Length: > 0 } authority ? authority : uri.Host;

    /// <summary>回报一次熔断结果：内置熔断器带上准入票，其它策略走原接口。</summary>
    private static void RecordCircuit(IAxiosCircuitPolicy policy, in AxiosCircuitContext context, long ticket)
    {
        if (policy is CircuitBreaker breaker)
        {
            breaker.Record(context, ticket);
            return;
        }

        policy.Record(context);
    }

    /// <summary>退回一次「已放行、但注定发不出去」的重试所扣的令牌。</summary>
    private static void RefundRetryBudget(IAxiosRetryPolicy policy) => (policy as RetryBudgetPolicy)?.Refund();
}
