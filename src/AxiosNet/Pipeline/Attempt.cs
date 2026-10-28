using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

/// <summary>
/// 内置尝试中间件的具名入口：<see cref="Retry"/> / <see cref="Failover"/> / <see cref="Hedging"/> /
/// <see cref="Circuit"/> / <see cref="Gate"/> 永远在默认链上，<see cref="Audit"/> 是可选内置项（要显式 `Append` 才生效）。
/// 它们是否真的干活由中间件自己看本次请求的生效配置决定（没配策略 / 端点 / 分支数就直接把请求交给下一环）——
/// 这样链才能在客户端构造时编译一次，不必每次请求重建。
/// </summary>
/// <remarks>
/// 这里的默认中间件只是<b>调度器</b>：重试判据在 <see cref="IAxiosRetryPolicy"/>、
/// 熔断判据在 <see cref="IAxiosCircuitPolicy"/>，对冲与换端点的开关在
/// <see cref="AxiosConfig.Hedging"/> / <see cref="AxiosConfig.Failover"/>。要改行为请换策略或配置，不要换中间件。
/// </remarks>
public static partial class Attempt
{
    /// <summary>
    /// 重试：最外层的循环型中间件。每次尝试换一条新请求（正文与请求头沿用模板），
    /// 没配策略或 <c>MaxRetries &lt;= 0</c> 时直接把请求交给下一环（不建状态机）。
    /// </summary>
    public static AxiosAttemptMiddleware Retry { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 快路径刻意不写成 async：默认配置下绝大多数请求不重试，不该为它多建一个状态机。
        return (request, ctx) =>
            ctx.Retry?.Policy is { } policy && policy.MaxRetries > 0
                ? RetryLoopAsync(next, request, ctx, policy)
                : next(request, ctx);
    };

    /// <summary>
    /// 熔断：每次尝试之前的闸门。拒绝时请求根本没发出去，因此排在并发闸门之前 —— 不给被拒的请求占槽位。
    /// 没配策略时直接把请求交给下一环。
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
    /// 并发闸门：本次尝试的槽位。排队时间不计入 <see cref="AxiosConfig.Timeout"/>（队列有自己的
    /// <c>QueueTimeout</c>），所以计时在闸门之后才开始。没设并发上限时直接把请求交给下一环。
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
    /// 重试循环：每次尝试有独立的超时预算（<see cref="AxiosConfig.Timeout"/> 是单次尝试上限），
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

        // 正文必须能拨回起点，否则重试只会发出空内容；在第一次发送之前就拦下。
        // 放在这里而不是建上下文时：管线里没有重试中间件就不该做这项检查。
        var bodyRewind = RetryBody.Prepare(ctx.Config);

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // 同一份正文再发一次：先把读指针拨回起点。放在 try 之外：拨不动说明正文不可重放，
                // 不该再拿去问策略要不要重试。
                bodyRewind?.Rewind(ctx.Config);
            }

            // 首次尝试直接用模板，后续尝试按模板复制（模板本身不能再发，也不该被后续尝试改写）
            var request = attempt == 1 ? template : CloneRequest(template);
            try
            {
                return await next(request, ctx.WithAttemptNumber(attempt)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消类异常直接冒到调用方，不问策略是否重试。
                var failed = new AxiosRetryContext(attempt, policy.MaxRetries, ex, ctx.Config, (ex as AxiosException)?.Response);

                if (!CanRetry(failed, externalToken, policy))
                {
                    throw;
                }

                var delay = policy.GetDelay(failed);
                if (delay < TimeSpan.Zero)
                {
                    // 策略算错时间不该把请求变成配置错误：负值按「不等待」处理。
                    delay = TimeSpan.Zero;
                }
                else if (delay > Timeouts.Max)
                {
                    // Task.Delay 上限约 49.7 天，自定义策略可返回任意值，在这里收敛。
                    delay = Timeouts.Max;
                }

                // 回调与遥测共用同一个上下文
                var retryContext = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, ctx.Config, failed.Response, delay);

                // 回调在 catch 块里调用：它抛出的异常按约定原样抛给调用方，不再问策略。
                retry.OnRetry?.Invoke(retryContext);

                // 观测点放在「决定重试」这一刻，与 OnRetry 同一语义。
                AxiosTelemetry.RequestRetried(retryContext, ctx.Uri);

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, externalToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce)
                    {
                        // 等待期间被取消：仍按统一错误模型报「已取消」，而不是漏出裸 TaskCanceledException
                        throw AxiosException.Canceled(ctx.Config, request, "请求在重试等待期间被取消", oce);
                    }
                }
            }
            finally
            {
                if (!ReferenceEquals(request, template))
                {
                    // 正文是共享的（后续尝试还要用）：先摘下来，只释放这条请求本身
                    request.Content = null;
                    request.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 带熔断的单次尝试：放行 → 发送 → 回报结果。拒绝时直接抛 <c>ERR_CIRCUIT_OPEN</c>；
    /// 回报必须恰好一次，漏掉「取消」会让熔断器卡在半开。
    /// </summary>
    private static async ValueTask<AxiosAttemptResult> WithCircuitAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        IAxiosCircuitPolicy policy)
    {
        // 调用方已取消就先报取消，否则熔断拒绝会盖掉真正的退出原因。
        if (ctx.ExternalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(ctx.Config, request);
        }

        var context = new AxiosCircuitContext(CircuitHost(ctx.Uri), ctx.Uri, ctx.Config);

        if (!policy.ShouldAllow(context))
        {
            // 建议等待时间：策略说不准时给零，错误消息里就不带「retry after」。
            var retryAfter = policy.GetRetryAfter(context);

            // 回调在抛错之前调用，其异常按约定原样抛出；上下文带上建议等待时间。
            ctx.Circuit!.OnRejected?.Invoke(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, retryAfter: retryAfter));

            throw AxiosException.CircuitOpen(ctx.Config, request, context.Host, retryAfter);
        }

        AxiosAttemptResult result;
        try
        {
            result = await next(request, ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 失败结论带上异常，策略据此区分下游故障与取消 / 4xx；策略异常按约定原样抛出。
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, ex));
            throw;
        }

        // 内部中间件漏返回时先给出可定位的错误：否则下面读 Status 会变成裸 NRE。
        result = AxiosAttemptResult.Ensure(result, ctx.Config);

        // 状态码一并回报（validateStatus 放行时也可能是 5xx）；流式响应在响应头到达时记账。
        // 回报放在 try 之外，策略异常不会被记成一次尝试失败。
        try
        {
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, statusCode: result.Response.Status));
        }
        catch
        {
            // 策略异常按约定原样抛给调用方，但不能顺带漏掉流式正文的租约 —— 它占着该 origin 的并发额度。
            result.StreamOwner?.Dispose();
            throw;
        }

        return result;
    }

    /// <summary>
    /// 并发槽位：占到的槽位写进上下文交给终端，由终端统一归还（失败路径）或接管（流式成功路径）。
    /// 排队用本次尝试的令牌，所以被放弃的尝试（例如对冲的败者）在闸门里排队时也能立刻收掉。
    /// </summary>
    /// <remarks>
    /// <b>闸门自己也要兜底归还</b>：终端只在「真的被调用」时才归还，而插在闸门之后的自定义中间件
    /// 完全可以不调 <c>next</c>（自己返回一份响应）或自己抛异常 —— 那时终端一次都不执行，
    /// 槽位就再也没人还了，后续请求会一直卡到 <c>QueueTimeout</c>。
    /// 槽位的 <see cref="IDisposable.Dispose"/> 是幂等的（内部用 <c>Interlocked.Exchange</c>），
    /// 所以「终端已归还 + 闸门再归还一次」是安全的重复释放，不会误伤别人的槽位。
    /// 自建 <see cref="AxiosAttemptResult.StreamOwner"/> 的中间件也算「不调终端」：闸门照常归还槽位，
    /// 自建凭证拿不到这份并发额度（只有库内流式凭证才会让槽位随正文继续占用）。
    /// </remarks>
    private static async ValueTask<AxiosAttemptResult> WithSlotAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        ConcurrencyGate gate)
    {
        var slot = await gate.AcquireAsync(
            ctx.Config, request, ctx.AttemptToken, ctx.Config.LimitsOrNull?.QueueTimeout).ConfigureAwait(false);

        AxiosAttemptResult result;
        try
        {
            result = await next(request, ctx.WithSlot(slot)).ConfigureAwait(false);
        }
        catch
        {
            // 失败路径：终端通常已经归还过了（幂等）；真正兜的是「内层根本没走到终端」的情况。
            slot.Dispose();
            throw;
        }

        // 成功路径：流式响应把槽位随正文转交给了 StreamOwner —— 此刻归还会让还在下载的正文
        // 不再计入并发额度。其余情况（含中间件自己造的响应与自建凭证）都归还，幂等。
        if (!result.SlotTransferred)
        {
            slot.Dispose();
        }

        return result;
    }

    /// <summary>按模板复制一条可发送的请求（请求头逐个搬运，正文沿用同一份）；用 <c>TryAddWithoutValidation</c> 避免对已校验过的头再校验一遍。</summary>
    /// <param name="template">请求模板（模板本身不能再发）。</param>
    /// <param name="uri">替换后的目标地址；<c>null</c> 表示沿用模板的地址（备用端点换镜像时用）。</param>
    internal static HttpRequestMessage CloneRequest(HttpRequestMessage template, Uri? uri = null)
    {
        var request = new HttpRequestMessage(template.Method, uri ?? template.RequestUri)
        {
            Version = template.Version,

            // 策略必须一起搬，否则 RequestVersionExact 会变回默认的 OrLower。
            VersionPolicy = template.VersionPolicy
        };

        // 换了目标地址就不搬 Host：HttpClient 只认 Headers.Host，搬过去会让镜像按原端点的虚拟主机路由。
        var retargeted = uri is not null;

        foreach (var header in template.Headers)
        {
            if (retargeted && string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // 自定义中间件写进 Options 的内容（如 HttpRequestOptions 上的开关）要跟着走，否则重发一次就丢。
        // HttpRequestOptions 的索引器是显式实现，Set 只认带类型的 HttpRequestOptionsKey，这里走字典接口。
        var options = (IDictionary<string, object?>)request.Options;
        foreach (var option in template.Options)
        {
            options[option.Key] = option.Value;
        }

        request.Content = template.Content;
        return request;
    }

    /// <summary>
    /// 这次失败是不是「本次尝试被内部收掉」而不是真失败：调用方没取消，但本次尝试的令牌已被链接令牌取消。
    /// 库内只有对冲会这么干（胜出后取消其余分支），所以这是「败者被清理」，不是下游故障 ——
    /// 对冲据此不记失败，审计据此不发 <c>AttemptFailed</c>（否则健康请求在监控里恒有一半失败）。
    /// 取消在库里的两种形态都要认（<see cref="OperationCanceledException"/> 与 <c>ERR_CANCELED</c>）。
    /// </summary>
    internal static bool IsCleanupCancellation(Exception exception, in AxiosAttemptContext ctx)
        => !ctx.ExternalToken.IsCancellationRequested
           && (exception is OperationCanceledException
               || exception is AxiosException { Code: AxiosException.ErrCanceled });

    /// <summary>库层面的「不该重试」：次数到顶、调用方已取消、错误即取消、被熔断拦下；其余交给策略判断。</summary>
    private static bool CanRetry(AxiosRetryContext context, CancellationToken externalToken, IAxiosRetryPolicy policy)
    {
        if (context.Attempt > policy.MaxRetries)
        {
            return false;
        }

        if (externalToken.IsCancellationRequested)
        {
            return false;
        }

        if (context.Exception is OperationCanceledException)
        {
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCanceled })
        {
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCircuitOpen })
        {
            // 熔断拒绝时请求根本没发出去，再重试只会撞同一个开着的闸门。
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrQueueTimeout })
        {
            // 并发闸门排队超时说明本实例在途已经顶到上限 —— 立刻重发只会再撞同一个满载的闸门，
            // 把「等并发降下来」这件该由调用方（降速 / 扩容 / 换实例）决定的事，变成库内的无效循环。
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrBadOptionValue })
        {
            // 尝试阶段的配置 / 编程错误（中间件返回 default、凭证用法不对等）：重发改变不了它。
            return false;
        }

        return policy.ShouldRetry(context);
    }

    /// <summary>熔断的计数维度：<c>host:port</c>（同一 host 的不同端口是两个独立下游），非分层 URI 退化为 host。</summary>
    private static string CircuitHost(Uri uri)
        => uri.Authority is { Length: > 0 } authority ? authority : uri.Host;
}
