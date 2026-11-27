using System.Runtime.ExceptionServices;

namespace AxiosNet.Pipeline;

// 「一次尝试」的备用端点：当前端点用不了时，按顺序把同一份请求嫁接到下一个端点重发。
public static partial class Attempt
{
    /// <summary>备用端点：当前端点不可用时换下一个端点重发，顺序即优先级；全部失败才把第一条失败交给外层。</summary>
    /// <remarks>
    /// 位置在 <see cref="Retry"/> 之内、<see cref="Hedging"/> 之外：每个端点各得一组对冲分支。端点表为空时纯直通；4xx、闸门拒绝、配置错误与取消不换端点，
    /// <c>ERR_CIRCUIT_OPEN</c> 换。要求正文能重放；版本不可行或拼出来就是当前地址的端点直接跳过；流式响应只在响应头阶段能换端点。
    /// </remarks>
    public static AxiosAttemptMiddleware Failover { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 未配备用端点时纯直通。
        return (request, ctx) =>
            ctx.Failover is { Endpoints.Count: > 0 } failover
                ? FailoverAsync(next, request, ctx, failover)
                : next(request, ctx);
    };

    /// <summary>换端点重发：按健康顺序（或原序）逐个试，只换「换个下游就能解决」的故障——取消、4xx、配置错误、闸门拒绝与总预算耗尽都不换。</summary>
    private static async ValueTask<AxiosAttemptResult> FailoverAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        AxiosConfig.FailoverSettings failover)
    {
        var endpoints = failover.Endpoints!;

        // 换端点重发同一份正文：流必须可拨回起点。
        var bodyRewind = RetryBody.Prepare(ctx.Config);

        Exception? firstFailure = null;

        // 健康记忆关闭时（阈值或冷却为 0）不读也不写。
        var health = ctx.FailoverHealth;
        var usesHealth = health is not null && failover.FailureThreshold > 0 && failover.Cooldown > TimeSpan.Zero;

        var order = usesHealth ? BuildOrder(template.RequestUri!, endpoints, health!) : null;

        // 只有真的发送过才需要拨回正文。
        var attempted = 0;

        // 第 0 轮是原始端点，之后依次是端点表里的备用端点；order 非空时按健康顺序走。
        for (var step = 0; step <= endpoints.Count; step++)
        {
            var round = order is null ? step : order[step];
            var key = usesHealth ? EndpointKey(template.RequestUri!, endpoints, round) : null;

            if (attempted > 0)
            {
                // 同一份正文再发一次：先拨回读指针。
                bodyRewind?.Rewind(ctx.Config);
            }

            var uri = round == 0 ? null : MirrorUri(template.RequestUri!, endpoints[round - 1]);

            // 拼出来就是当前地址，跳过。
            if (uri is not null && uri.Equals(template.RequestUri))
            {
                continue;
            }

            // 换端点可能换掉 scheme：重新校验当前 HTTP 版本是否可行。
            if (uri is not null)
            {
                try
                {
#if NETSTANDARD2_0
                    // 兼容资产没有 VersionPolicy，校验在版本 >1.1 时一律拒绝。
                    AxiosClient.ValidateHttp3Request(uri, template.Version, default, ctx.Config);
#else
                    AxiosClient.ValidateHttp3Request(uri, template.Version, template.VersionPolicy, ctx.Config);
#endif
                }
                catch (AxiosException ex) when (ex.Code == AxiosException.ErrBadOptionValue)
                {
                    // 跳过不算失败，仅在无失败记录时兜底。
                    firstFailure ??= ex;
                    continue;
                }
            }

            var request = uri is null ? template : CloneRequest(template, uri);
            var attemptCtx = uri is null ? ctx : ctx.WithUri(uri).WithEndpointIndex(round);

            try
            {
                attempted++;

                var result = await next(request, attemptCtx).ConfigureAwait(false);

                // 端点给出成功响应：解除冷却。
                if (key is not null)
                {
                    health!.RecordSuccess(key);
                }

                return result;
            }
            catch (Exception ex) when (IsGateRejection(ex) && firstFailure is { } realFailure)
            {
                // 闸门是客户端级的，抛已记下的真实失败。
                ExceptionDispatchInfo.Capture(realFailure).Throw();

                // Throw() 不返回。
                throw;
            }
            catch (AxiosException ex) when (ex.IsTotalTimeoutExceeded)
            {
                // 预算已耗尽：换端点也注定超时。
                throw;
            }
            catch (Exception ex) when (CanFailover(ex, ctx.ExternalToken))
            {
                // 该端点用不了：记一笔失败后继续下一个端点。
                if (key is not null)
                {
                    health!.RecordFailure(key, failover.FailureThreshold, failover.Cooldown, failover.MaxCooldown);
                }

                firstFailure ??= ex;
            }
            catch (AxiosException ex) when (key is not null && IsHealthyEvidence(ex))
            {
                // 4xx 说明端点活着：清掉失败记录，错误照常交给外层（不换端点）。
                health!.RecordSuccess(key);
                throw;
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

        // 所有端点都失败：交给外层重试。
        throw firstFailure!;
    }

    /// <summary>轮次顺序：健康的端点按原顺序在前，冷却中的按原顺序退到末尾；全部健康时返回 <c>null</c>（按原顺序走）。</summary>
    private static int[]? BuildOrder(Uri requestUri, IReadOnlyList<Uri> endpoints, FailoverHealth health)
    {
        var total = endpoints.Count + 1;
        var anyCooling = false;

        // 第一遍只判定是否需要重排。
        for (var round = 0; round < total; round++)
        {
            if (!health.IsAvailable(EndpointKey(requestUri, endpoints, round)))
            {
                anyCooling = true;
                break;
            }
        }

        if (!anyCooling)
        {
            return null;
        }

        var order = new int[total];
        var at = 0;

        // 第二遍放健康端点（保持原相对顺序），第三遍把冷却中的追加到末尾。
        for (var round = 0; round < total; round++)
        {
            if (health.IsAvailable(EndpointKey(requestUri, endpoints, round)))
            {
                order[at++] = round;
            }
        }

        for (var round = 0; round < total; round++)
        {
            if (!health.IsAvailable(EndpointKey(requestUri, endpoints, round)))
            {
                order[at++] = round;
            }
        }

        return order;
    }

    /// <summary>健康表的记账键：<c>0</c> 取请求 origin，备用端点取其 base URL（可带路径前缀）。</summary>
    private static string EndpointKey(Uri requestUri, IReadOnlyList<Uri> endpoints, int round)
        => round == 0
            ? requestUri.GetLeftPart(UriPartial.Authority)
            : endpoints[round - 1].GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>4xx 是「端点活着、拒绝了这个请求」：可以清健康记录，但不该换端点。</summary>
    private static bool IsHealthyEvidence(AxiosException exception)
        => exception.Status is >= 400 and < 500;

    /// <summary>换端点判据：只换「换一个下游就能解决」的故障；取消、配置错误、闸门拒绝、总预算耗尽与 4xx 都不换。</summary>
    private static bool CanFailover(Exception exception, CancellationToken externalToken)
    {
        if (externalToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return false;
        }

        if (exception is not AxiosException axios)
        {
            return false;
        }

        return axios.Code switch
        {
            // 建连 / DNS / TLS / 读响应失败、本机连接池耗尽。
            AxiosException.ErrNetwork or AxiosException.ErrPoolExhausted => true,

            // 发出之后的超时；预算已耗尽的超时不再换。
            AxiosException.ErrConnAborted when !axios.IsTotalTimeoutExceeded => true,

            // 熔断按端点独立成账。
            AxiosException.ErrCircuitOpen => true,

            // 仅 5xx 换端点。
            AxiosException.ErrBadResponse => axios.Status is >= 500,

            _ => false
        };
    }

    /// <summary>
    /// 把请求的 path / query 嫁接到备用端点的 base URL 上（端点可带路径前缀）。
    /// 例：<c>https://primary/v1/users?page=2</c> + <c>https://mirror/api</c> → <c>https://mirror/api/v1/users?page=2</c>。
    /// </summary>
    internal static Uri MirrorUri(Uri requestUri, Uri endpoint)
    {
        var baseUrl = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var pathAndQuery = requestUri.PathAndQuery;

        // path 恒以 '/' 开头，拼接不会出现双斜杠。
        return new Uri(baseUrl + pathAndQuery, UriKind.Absolute);
    }
}
