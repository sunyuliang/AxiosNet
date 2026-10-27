using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的发送循环：重试、熔断与请求复制。
public sealed partial class AxiosClient
{
    /// <summary>一次请求在「发送阶段」不变的输入，打成结构体收敛发送方法的入参；不含 <see cref="HttpRequestMessage"/>（它逐次尝试变，见 <see cref="CloneRequest"/>）。</summary>
    private readonly struct SendContext
    {
        private SendContext(
            AxiosConfig config,
            Uri uri,
            CancellationToken externalToken,
            Type? dataType,
            AxiosResponse? target,
            bool streamResponse,
            AxiosConfig.RetrySettings? retry,
            AxiosConfig.CircuitSettings? circuit,
            RetryBody? bodyRewind,
            ConcurrencyGate? gate)
        {
            Config = config;
            Uri = uri;
            ExternalToken = externalToken;
            DataType = dataType;
            Target = target;
            StreamResponse = streamResponse;
            Retry = retry;
            Circuit = circuit;
            BodyRewind = bodyRewind;
            Gate = gate;
        }

        /// <summary>唯一创建入口：派生项（令牌 / 流式标记 / 重试与熔断配置 / 正文可重放性）在这里一次算清。</summary>
        public static SendContext From(
            AxiosConfig config,
            Uri uri,
            Type? dataType,
            AxiosResponse? target,
            ConcurrencyGate? gate)
        {
            var retry = config.RetryOrNull;

            return new SendContext(
                config,
                uri,
                config.CancellationToken,
                dataType,
                target,
                config.ResponseType == ResponseType.Stream,
                retry,
                config.CircuitOrNull,

                // 来自流的正文必须能拨回起点，否则重试只会发出空内容；在发请求之前就拦下。
                retry?.Policy is { MaxRetries: > 0 } ? RetryBody.Prepare(config) : null,
                gate);
        }

        /// <summary>本次请求的生效配置（合并 + 拦截器之后的最终形态）。</summary>
        public AxiosConfig Config { get; }

        /// <summary>最终 URL：错误上下文与遥测标签都用它。</summary>
        public Uri Uri { get; }

        /// <summary>调用方令牌：贯穿所有尝试与退避等待。</summary>
        public CancellationToken ExternalToken { get; }

        /// <summary>泛型入口的目标类型（非泛型入口为 <c>null</c>），决定反序列化成什么。</summary>
        public Type? DataType { get; }

        /// <summary>调用方预造的响应对象：管线就地填充，省掉一次类型转换与再分配。</summary>
        public AxiosResponse? Target { get; }

        /// <summary>是否流式响应（<c>ResponseType == Stream</c>）。</summary>
        public bool StreamResponse { get; }

        /// <summary>重试配置：<c>null</c> = 不重试（默认）。</summary>
        public AxiosConfig.RetrySettings? Retry { get; }

        /// <summary>熔断配置：<c>null</c> = 不熔断（默认）。</summary>
        public AxiosConfig.CircuitSettings? Circuit { get; }

        /// <summary>正文可重放性检查：只在「真的会重试」时才建，否则为 <c>null</c>。</summary>
        public RetryBody? BodyRewind { get; }

        /// <summary>并发闸门：<c>null</c> = 本次不限并发。</summary>
        public ConcurrencyGate? Gate { get; }
    }

    /// <summary>带重试的发送入口：没有策略时直接交回单次发送的 Task。</summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithRetryAsync(
        HttpRequestMessage template,
        SendContext ctx)
    {
        var policy = ctx.Retry?.Policy;
        if (policy is null || policy.MaxRetries <= 0)
        {
            return SendAttemptAsync(template, ctx);
        }

        return SendRetryLoopAsync(template, ctx, policy);
    }

    /// <summary>重试循环（只在配置了策略时走到）：每次尝试换一条新请求，正文与请求头沿用模板；每次尝试有独立的超时预算（<see cref="AxiosConfig.Timeout"/> 是单次尝试上限），调用方令牌贯穿所有尝试与等待。</summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendRetryLoopAsync(
        HttpRequestMessage template,
        SendContext ctx,
        IAxiosRetryPolicy policy)
    {
        var retry = ctx.Retry!;
        var externalToken = ctx.ExternalToken;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // 同一份正文再发一次：先把读指针拨回起点。放在 try 之外：拨不动说明正文不可重放，
                // 不该再拿去问策略要不要重试。
                ctx.BodyRewind?.Rewind(ctx.Config);
            }

            // 首次尝试直接用模板，后续尝试按模板复制（模板本身不能再发，也不该被后续尝试改写）
            var request = attempt == 1 ? template : CloneRequest(template);
            try
            {
                return await SendAttemptAsync(request, ctx).ConfigureAwait(false);
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

    /// <summary>一次尝试的入口：先过熔断闸门，再发送；没有熔断策略时直接转给 <see cref="SendCoreAsync"/>。</summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendAttemptAsync(
        HttpRequestMessage request,
        SendContext ctx)
    {
        if (ctx.Circuit?.Policy is not { } policy)
        {
            return SendCoreAsync(request, ctx);
        }

        return SendWithCircuitAsync(request, ctx, policy);
    }

    /// <summary>带熔断的单次尝试：放行 → 发送 → 回报结果。拒绝时直接抛 <c>ERR_CIRCUIT_OPEN</c>；回报必须恰好一次，漏掉「取消」会让熔断器卡在半开。</summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithCircuitAsync(
        HttpRequestMessage request,
        SendContext ctx,
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

        AxiosResponse response;
        IDisposable? streamOwner;

        try
        {
            (response, streamOwner) = await SendCoreAsync(request, ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 失败结论带上异常，策略据此区分下游故障与取消 / 4xx；策略异常按约定原样抛出。
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, ex));
            throw;
        }

        // 状态码一并回报（validateStatus 放行时也可能是 5xx）；流式响应在响应头到达时记账。
        // 回报放在 try 之外，策略异常不会被记成一次尝试失败。
        try
        {
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, statusCode: response.Status));
        }
        catch
        {
            // 策略异常按约定原样抛给调用方，但不能顺带漏掉流式正文的租约 —— 它占着该 origin 的并发额度。
            streamOwner?.Dispose();
            throw;
        }

        return (response, streamOwner);
    }

    /// <summary>熔断的计数维度：<c>host:port</c>（同一 host 的不同端口是两个独立下游），非分层 URI 退化为 host。</summary>
    private static string CircuitHost(Uri uri)
        => uri.Authority is { Length: > 0 } authority ? authority : uri.Host;

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

        return policy.ShouldRetry(context);
    }

    /// <summary>按模板复制一条可发送的请求（请求头逐个搬运，正文沿用同一份）；用 <c>TryAddWithoutValidation</c> 避免对已校验过的头再校验一遍。</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage template)
    {
        var request = new HttpRequestMessage(template.Method, template.RequestUri)
        {
            Version = template.Version,

            // 策略必须一起搬，否则 RequestVersionExact 会变回默认的 OrLower。
            VersionPolicy = template.VersionPolicy
        };

        foreach (var header in template.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = template.Content;
        return request;
    }
}
