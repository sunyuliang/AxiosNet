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
    /// <para>
    /// 位置在 <see cref="Retry"/> 之内、<see cref="Hedging"/> 之外：每个端点各得一组对冲分支，
    /// 也只有把当前端点的那组分支都跑完才换端点。换端点走 <see cref="AxiosAttemptContext.WithUri"/> 与
    /// <see cref="AxiosAttemptContext.WithEndpointIndex"/>，于是连接租约、熔断计数维度、遥测标签与审计事件里的
    /// 端点序号都跟着新端点走（熔断按端点独立成账）。
    /// </para>
    /// <para>
    /// 未配置（端点表为 <c>null</c> / 空）时是纯直通。4xx、<c>ERR_QUEUE_TIMEOUT</c> / <c>ERR_QUEUE_FULL</c>、
    /// 配置 / 编程错误与取消都不换端点 —— 闸门是客户端级的，换镜像改变不了它们；
    /// <c>ERR_CIRCUIT_OPEN</c> 换（熔断按端点独立成账）。
    /// 换端点途中撞上闸门拒绝时，若前面已经记下真实失败，抛出的仍是那条真实失败（与对冲同一口径），
    /// 不会用「没排上队」盖掉能定位问题的故障。
    /// 换端点要求正文能按顺序重放，与重试同一份要求；跑不了当前 HTTP 版本的端点（如明文镜像 + 严格 h3）、
    /// 以及拼出来就是当前地址的端点（同 origin 且不带路径前缀）都直接跳过并继续下一个，
    /// 全部端点都不可用时才把第一条错误抛出。
    /// 流式响应只在响应头阶段能换端点：正文交给调用方之后，中途断流不会再换端点。
    /// </para>
    /// </remarks>
    public static AxiosAttemptMiddleware Failover { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 快路径不建状态机：没配备用端点时是纯直通。
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

        // 换端点会按顺序重发同一份正文：与重试同一份要求（流必须可拨回起点）。
        var bodyRewind = RetryBody.Prepare(ctx.Config);

        Exception? firstFailure = null;

        // 第 0 轮是原始端点，之后依次是端点表里的备用端点。
        for (var round = 0; round <= endpoints.Count; round++)
        {
            if (round > 0)
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

            // 换端点会换掉 scheme（https → http 很常见）：h3 的可行性只对原目标判过，这里按同一套护栏重判，
            // 否则严格策略会退化成发送时的 ERR_NETWORK，调用方看不出是配置问题。
            // 这个端点跑不了当前 HTTP 版本就换下一个：一条配错的端点不该把后面能用的端点一起废掉。
            if (uri is not null)
            {
                try
                {
                    AxiosClient.ValidateHttp3Request(uri, template.Version, template.VersionPolicy, ctx.Config);
                }
                catch (AxiosException ex) when (ex.Code == AxiosException.ErrBadOptionValue)
                {
                    // 跳过不算一次失败，不覆盖已记下的那条；只在一个失败都没记下时兜住，免得最后抛空引用。
                    firstFailure ??= ex;
                    continue;
                }
            }

            var request = uri is null ? template : CloneRequest(template, uri);
            var attemptCtx = uri is null ? ctx : ctx.WithUri(uri).WithEndpointIndex(round);

            try
            {
                return await next(request, attemptCtx).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsGateRejection(ex) && firstFailure is { } realFailure)
            {
                // 备用端点连槽位都没拿到（排队超时 / 队列满）：闸门是客户端级的，换下一个端点也过不去；
                // 它说明不了下游健康与否，抛已记下的真实失败，别让它盖掉能定位问题的那一条（与对冲同一口径）。
                ExceptionDispatchInfo.Capture(realFailure).Throw();

                // 不可达：Throw() 不返回，编译器不知道。
                throw;
            }
            catch (Exception ex) when (CanFailover(ex, ctx.ExternalToken))
            {
                // 这个端点用不了：记下第一条失败（决定最终错误），继续下一个端点。
                firstFailure ??= ex;
            }
            finally
            {
                if (!ReferenceEquals(request, template))
                {
                    // 正文是共享的：先摘下来，只释放这条请求本身。
                    request.Content = null;
                    request.Dispose();
                }
            }
        }

        // 所有端点都失败：交回重试（它决定要不要再来一轮），没有重试就是调用方看到的错误。
        throw firstFailure!;
    }

    /// <summary>
    /// 换端点判据：只换「换一个下游就能解决」的故障。取消、配置 / 编程错误、闸门拒绝（排队超时 / 队列满）、
    /// 总预算耗尽与 4xx 换镜像都改变不了，原样抛给上层；熔断拒绝要换 —— 熔断按 <c>host:port</c> 独立成账，
    /// 主端点开着的那一个挡不住备用端点。
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
            // 建连 / DNS / TLS / 读响应失败、本机连接池耗尽：换个下游都可能好。
            AxiosException.ErrNetwork or AxiosException.ErrPoolExhausted => true,

            // 发出之后的超时：这个下游不响应，换一个可能响应。
            // 预算耗尽（IsTotalTimeoutExceeded）不在其列：TotalTimeout 管的是整条逻辑请求的墙钟时间，
            // 换端点不会让它变长 —— 只会在备用端点再走一遍管线、占一次熔断名额与闸门入口，
            // 然后必然在同一处立刻失败。
            AxiosException.ErrConnAborted when !axios.IsTotalTimeoutExceeded => true,

            // 熔断按端点独立成账：这个端点被自己的熔断器拦下，不代表下一个端点也被拦下。
            AxiosException.ErrCircuitOpen => true,

            // 5xx 说明下游自身故障；其余 ERR_BAD_RESPONSE（如 2xx 的畸形正文）换端点解决不了。
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
