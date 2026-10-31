using System.Runtime.ExceptionServices;
using AxiosNet.Internal;

namespace AxiosNet.Pipeline;

// 「一次尝试」的备用端点：当前端点用不了时，按顺序把同一份请求嫁接到下一个端点重发。
public static partial class Attempt
{
    /// <summary>
    /// 备用端点：当前端点「用不了」时（网络错误 / 超时 / 连接池耗尽 / 熔断打开 / 5xx）换下一个端点重发，
    /// 顺序即优先级；全部端点都失败才把第一条失败交给外层重试。
    /// </summary>
    /// <remarks>
    /// 位置在 <see cref="Retry"/> 之内、<see cref="Hedging"/> 之外：每个端点各得一组对冲分支。
    /// 换端点走 <see cref="AxiosAttemptContext.WithUri"/> 与 <see cref="AxiosAttemptContext.WithEndpointIndex"/>，
    /// 连接租约、熔断计数维度与审计事件里的地址 / 端点序号都跟着新端点走；
    /// 请求级 span 与本库的重试指标 / 重试事件仍按逻辑地址记（拿不到分支实际连的主机）。
    /// 端点表为 <c>null</c> / 空时是纯直通；4xx、<c>ERR_QUEUE_TIMEOUT</c> / <c>ERR_QUEUE_FULL</c>、
    /// 配置 / 编程错误与取消都不换端点，<c>ERR_CIRCUIT_OPEN</c> 换。
    /// 换端点要求正文能按顺序重放；跑不了当前 HTTP 版本的端点、以及拼出来就是当前地址的端点都直接跳过。
    /// 流式响应只在响应头阶段能换端点。
    /// </remarks>
    public static AxiosAttemptMiddleware Failover { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 没配备用端点时纯直通。
        return (request, ctx) =>
            ctx.Failover is { Endpoints.Count: > 0 } failover
                ? FailoverAsync(next, request, ctx, failover)
                : next(request, ctx);
    };

    private static async ValueTask<AxiosAttemptResult> FailoverAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        AxiosConfig.FailoverSettings failover)
    {
        var endpoints = failover.Endpoints!;

        // 换端点按顺序重发同一份正文：流必须可拨回起点。
        var bodyRewind = RetryBody.Prepare(ctx.Config);

        Exception? firstFailure = null;

        // 健康记忆：关掉时（阈值 0 / 冷却 0）不读也不写，行为与「每次从第一个端点开始」一致。
        var health = ctx.FailoverHealth;
        var usesHealth = health is not null && failover.FailureThreshold > 0 && failover.Cooldown > TimeSpan.Zero;

        var order = usesHealth ? BuildOrder(template.RequestUri!, endpoints, health!) : null;

        // 只有「真的发送过」才需要拨回正文：被跳过的端点没读过流。
        var attempted = 0;

        // 第 0 轮是原始端点，之后依次是端点表里的备用端点；order 非空时按健康顺序走。
        for (var step = 0; step <= endpoints.Count; step++)
        {
            var round = order is null ? step : order[step];
            var key = usesHealth ? EndpointKey(template.RequestUri!, endpoints, round) : null;

            if (attempted > 0)
            {
                // 同一份正文再发一次：先把读指针拨回起点。
                bodyRewind?.Rewind(ctx.Config);
            }

            var uri = round == 0 ? null : MirrorUri(template.RequestUri!, endpoints[round - 1]);

            // 端点拼出来就是当前地址（同 origin 且端点不带路径前缀）：换过去等于原地重发，跳过。
            if (uri is not null && uri.Equals(template.RequestUri))
            {
                continue;
            }

            // 换端点会换掉 scheme：按同一套 h3 护栏重判当前 HTTP 版本是否可行，不行就换下一个端点。
            if (uri is not null)
            {
                try
                {
                    AxiosClient.ValidateHttp3Request(uri, template.Version, template.VersionPolicy, ctx.Config);
                }
                catch (AxiosException ex) when (ex.Code == AxiosException.ErrBadOptionValue)
                {
                    // 跳过不算一次失败，只在一个失败都没记下时兜住。
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

                // 端点给出成功响应：立刻解除冷却（4xx 由下面的 catch 处理，同样算健康证据）。
                if (key is not null)
                {
                    health!.RecordSuccess(key);
                }

                return result;
            }
            catch (Exception ex) when (IsGateRejection(ex) && firstFailure is { } realFailure)
            {
                // 备用端点连槽位都没拿到（排队超时 / 队列满）：闸门是客户端级的，抛已记下的真实失败。
                ExceptionDispatchInfo.Capture(realFailure).Throw();

                // Throw() 不返回，编译器不知道。
                throw;
            }
            catch (Exception ex) when (CanFailover(ex, ctx.ExternalToken))
            {
                // 这个端点用不了：记一笔失败（到阈值后进入冷却），继续下一个端点。
                if (key is not null)
                {
                    health!.RecordFailure(key, failover.FailureThreshold, failover.Cooldown, failover.MaxCooldown);
                }

                firstFailure ??= ex;
            }
            catch (AxiosException ex) when (key is not null && IsHealthyEvidence(ex))
            {
                // 4xx 说明端点本身是活的：清掉它的失败记录，然后把错误照常交给外层（不换端点）。
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

        // 所有端点都失败：交回重试（它决定要不要再来一轮），没有重试就是调用方看到的错误。
        throw firstFailure!;
    }

    /// <summary>
    /// 轮次顺序：健康的端点按原顺序在前，冷却中的端点按原顺序退到末尾。
    /// 全部健康时返回 <c>null</c>（按原顺序走，稳态零分配）。
    /// </summary>
    private static int[]? BuildOrder(Uri requestUri, IReadOnlyList<Uri> endpoints, FailoverHealth health)
    {
        var total = endpoints.Count + 1;
        var anyCooling = false;
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

    /// <summary>
    /// 健康表的记账键：<c>0</c> 用请求的 origin（同一后端的业务路径不算区别），
    /// 备用端点用它的 base URL（可以带路径前缀，不同前缀可能是不同后端）。
    /// </summary>
    private static string EndpointKey(Uri requestUri, IReadOnlyList<Uri> endpoints, int round)
        => round == 0
            ? requestUri.GetLeftPart(UriPartial.Authority)
            : endpoints[round - 1].GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>4xx 是「端点活着、拒绝了这个请求」：可以清健康记录，但不该换端点。</summary>
    private static bool IsHealthyEvidence(AxiosException exception)
        => exception.Status is >= 400 and < 500;

    /// <summary>
    /// 换端点判据：只换「换一个下游就能解决」的故障。取消、配置 / 编程错误、闸门拒绝（排队超时 / 队列满）、
    /// 总预算耗尽与 4xx 都不换；熔断拒绝要换（按 <c>host:port</c> 独立成账）。
    /// </summary>
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
            // 建连 / DNS / TLS / 读响应失败、本机连接池耗尽：换下游可能好。
            AxiosException.ErrNetwork or AxiosException.ErrPoolExhausted => true,

            // 发出之后的超时：换一个下游可能响应；总预算耗尽不换。
            AxiosException.ErrConnAborted when !axios.IsTotalTimeoutExceeded => true,

            // 熔断按端点独立成账：这个端点被拦下不代表下一个端点也被拦下。
            AxiosException.ErrCircuitOpen => true,

            // 5xx 说明下游自身故障；其余 ERR_BAD_RESPONSE（如 2xx 的畸形正文）不换。
            AxiosException.ErrBadResponse => axios.Status is >= 500,

            _ => false
        };
    }

    /// <summary>
    /// 把请求的 path / query 嫁接到备用端点的 base URL 上（端点可以带路径前缀）。
    /// 例：请求 <c>https://primary/v1/users?page=2</c>，端点 <c>https://mirror/api</c> →
    /// <c>https://mirror/api/v1/users?page=2</c>。
    /// </summary>
    internal static Uri MirrorUri(Uri requestUri, Uri endpoint)
    {
        var baseUrl = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var pathAndQuery = requestUri.PathAndQuery;

        // path 恒以 '/' 开头；端点本身以 '/' 结尾时其 path 就是 "/"，拼起来不会出现双斜杠。
        return new Uri(baseUrl + pathAndQuery, UriKind.Absolute);
    }
}
