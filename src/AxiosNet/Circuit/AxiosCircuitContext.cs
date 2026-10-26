namespace AxiosNet.Circuit;

/// <summary>
/// 一次尝试的熔断上下文：<see cref="IAxiosCircuitPolicy.ShouldAllow"/> 据此决定放不放行，
/// <see cref="IAxiosCircuitPolicy.Record"/> 据此判断这次尝试算「下游故障」还是「与下游无关」。
/// <para>
/// 是只读结构体（栈上传递，不进 GC），自定义策略可以直接按需读字段。
/// </para>
/// <para>
/// <b>失败口径与重试门禁对齐</b>：只有「下游真的不可用」才算失败 ——
/// 网络错误 / 超时（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c>）与 5xx 计入失败；
/// 取消（<c>ERR_CANCELED</c>）、4xx（含 408 / 429）、配置 / URL / 正文序列化错误一律不计
/// （见 <see cref="IsFailure"/>）。理由：4xx 是「这个请求本身有问题」，把它算成下游故障会让一个
/// 写错的接口把所有正常请求一起熔断；限流（429）该由重试的 <c>Retry-After</c> 消化，不该把下游判死。
/// </para>
/// </summary>
public readonly struct AxiosCircuitContext
{
    /// <param name="host">计数维度：默认是 <c>uri.Authority</c>（<c>host:port</c>），见 <see cref="Host"/>。</param>
    /// <param name="uri">本次请求的最终地址（已合并 baseURL 与 params）。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="exception">
    /// 本次尝试抛出的异常；<c>null</c> 表示请求拿到了响应且被 <c>validateStatus</c> 接受
    /// （是否算成功还要看 <paramref name="statusCode"/>，见 <see cref="IsSuccess"/>）。
    /// </param>
    /// <param name="retryAfter">被熔断拒绝时的建议等待（只有 <see cref="AxiosConfig.CircuitSettings.OnRejected"/> 回调里有值）。</param>
    /// <param name="statusCode">
    /// 本次尝试拿到的 HTTP 状态码（<c>validateStatus</c> 放行的响应没有异常，状态码只能由调用方补上）；
    /// 没有响应时为 <c>null</c>。显式传入时优先于异常里携带的状态码。
    /// </param>
    public AxiosCircuitContext(
        string host,
        Uri uri,
        AxiosConfig? config = null,
        Exception? exception = null,
        TimeSpan retryAfter = default,
        int? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(uri);

        Host = host;
        Uri = uri;
        Config = config;
        Exception = exception;
        RetryAfter = retryAfter;

        // 状态码在这里固定下来：与 AxiosRetryContext 同理，响应对象可能被后续尝试复用/改写。
        StatusCode = statusCode ?? (exception as AxiosException)?.Status;
    }

    /// <summary>
    /// 计数维度，默认取 <c>uri.Authority</c>（<c>host:port</c>）：同一 host 的不同端口是两个独立下游。
    /// <para>
    /// 想在别的维度上熔断（例如按 host 合并端口、按「host + 路由前缀」分组）时，
    /// 用 <see cref="CircuitBreaker.KeySelector"/> 改写；自定义策略可以直接读 <see cref="Uri"/> 自己算。
    /// </para>
    /// </summary>
    public string Host { get; }

    /// <summary>本次请求的最终地址（已合并 baseURL 与 params，与线上请求行一致）。</summary>
    public Uri Uri { get; }

    /// <summary>本次请求的生效配置（已合并实例默认值、已跑完请求拦截器）。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>本次尝试抛出的异常；<c>null</c> 表示成功。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// HTTP 状态码（有响应时可用）：<b>构造时的快照</b>，不会随下一次尝试改变。
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>库内错误码（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / <c>ERR_CIRCUIT_OPEN</c> 等）；非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode => (Exception as AxiosException)?.Code;

    /// <summary>请求方法。</summary>
    public HttpMethod? Method => Config?.Method;

    /// <summary>
    /// 被熔断拒绝时的建议等待时间（策略给出的估计）。
    /// <para>
    /// 只有 <see cref="AxiosConfig.CircuitSettings.OnRejected"/> 回调里是有效值：
    /// 其余场合（放行判定、结果回报）读到的总是 <see cref="TimeSpan.Zero"/>。
    /// 回调里想写「还要等多久」的日志就读它，不必等异常对象。
    /// </para>
    /// </summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>
    /// 本次尝试是否成功：拿到响应（或手工构造的上下文没有异常），且状态码不是 4xx / 5xx。
    /// <para>
    /// <c>validateStatus</c> 放行的非 2xx 不再一律算成功：4xx 与 5xx 保持与「抛异常那条路」一致的口径
    /// （见 <see cref="IsFailure"/>），否则同一个 500 会因 <c>validateStatus</c> 的配置不同而记出两种账。
    /// 3xx 视为成功（未跟随的重定向是终态响应）。
    /// </para>
    /// </summary>
    public bool IsSuccess => Exception is null && (StatusCode is null || StatusCode < 400);

    /// <summary>
    /// 本次尝试是否算<b>下游故障</b>（熔断计数的唯一口径）。
    /// <para>
    /// 计入：网络错误 / 超时（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c>）、<see cref="HttpRequestException"/>、
    /// <see cref="IOException"/>、以及 <b>5xx</b>。
    /// </para>
    /// <para>
    /// 不计入：取消（<see cref="OperationCanceledException"/> / <c>ERR_CANCELED</c>）、
    /// <b>全部 4xx</b>（含 408 / 429）、配置 / URL / 正文序列化 / 反序列化类错误 ——
    /// 它们说明的是「这次请求有问题」，不是「下游挂了」。
    /// </para>
    /// <para>
    /// 自定义策略可以不用它（例如坚持把 429 也算成故障），但对齐这个口径才能与重试门禁保持一致：
    /// 重试不会重试的东西，熔断也不该记在账上。
    /// </para>
    /// </summary>
    public bool IsFailure
    {
        get
        {
            // 有响应：只认 5xx。4xx（含 408 / 429）是请求侧问题。
            // 这里必须先看状态码：validateStatus 放行 5xx 时异常为 null，但状态码照样代表下游故障。
            if (StatusCode is int status)
            {
                return status >= 500;
            }

            if (Exception is null)
            {
                return false;
            }

            if (Exception is OperationCanceledException)
            {
                return false;
            }

            return Exception switch
            {
                AxiosException { Code: AxiosException.ErrNetwork or AxiosException.ErrConnAborted } => true,
                HttpRequestException => true,
                IOException => true,
                _ => false
            };
        }
    }
}
