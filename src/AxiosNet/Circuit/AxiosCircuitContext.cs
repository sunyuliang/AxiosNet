namespace AxiosNet.Circuit;

/// <summary>
/// 一次尝试的熔断上下文：<see cref="IAxiosCircuitPolicy.ShouldAllow"/> 据此决定放不放行，
/// <see cref="IAxiosCircuitPolicy.Record"/> 据此判断这次尝试算不算下游故障（只读结构体，栈上传递）。
/// </summary>
public readonly struct AxiosCircuitContext
{
    /// <param name="host">计数维度：默认是 <c>uri.Authority</c>（<c>host:port</c>）。</param>
    /// <param name="uri">本次请求的最终地址（已合并 baseURL 与 params）。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="exception">本次尝试抛出的异常；<c>null</c> 表示拿到了响应且被 <c>validateStatus</c> 接受。</param>
    /// <param name="retryAfter">被熔断拒绝时的建议等待（只有 <see cref="AxiosConfig.CircuitSettings.OnRejected"/> 回调里有值）。</param>
    /// <param name="statusCode">本次尝试拿到的 HTTP 状态码（无响应为 <c>null</c>）；显式传入时优先于异常里携带的状态码。</param>
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

        // 状态码在这里固定下来：响应对象可能被后续尝试复用 / 改写。
        StatusCode = statusCode ?? (exception as AxiosException)?.Status;
    }

    /// <summary>
    /// 计数维度，默认取 <c>uri.Authority</c>（<c>host:port</c>）；可用 <see cref="CircuitBreaker.KeySelector"/> 改写。
    /// </summary>
    public string Host { get; }

    /// <summary>本次请求的最终地址（已合并 baseURL 与 params，与线上请求行一致）。</summary>
    public Uri Uri { get; }

    /// <summary>本次请求的生效配置（已合并实例默认值、已跑完请求拦截器）。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>本次尝试抛出的异常；<c>null</c> 表示成功。</summary>
    public Exception? Exception { get; }

    /// <summary>HTTP 状态码（有响应时可用），构造时的快照。</summary>
    public int? StatusCode { get; }

    /// <summary>库内错误码（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / <c>ERR_CIRCUIT_OPEN</c> 等）；非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode => (Exception as AxiosException)?.Code;

    /// <summary>请求方法。</summary>
    public HttpMethod? Method => Config?.Method;

    /// <summary>被熔断拒绝时的建议等待时间（只有 <see cref="AxiosConfig.CircuitSettings.OnRejected"/> 回调里有值）。</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>本次尝试是否成功：拿到响应，且状态码不是 4xx / 5xx（3xx 视为成功）。</summary>
    public bool IsSuccess => Exception is null && (StatusCode is null || StatusCode < 400);

    /// <summary>
    /// 本次尝试是否「一次都没发出」：<see cref="AxiosConfig.TotalTimeout"/> 在排队或入口处就耗尽了
    /// （<c>ECONNABORTED</c>，但下游连一个字节都没收到）。
    /// </summary>
    /// <remarks>
    /// 与「发出后超时」区分开：那个说明下游不响应，是真的故障；这个只说明本地预算先到期
    /// —— 过载时排队变长就足以让每条请求都撞上它。自定义策略若要自己记账，用它排除。
    /// </remarks>
    public bool TotalTimeoutExpiredBeforeSend => Exception is AxiosException { IsTotalTimeoutExceeded: true };

    /// <summary>
    /// 本次尝试是否算下游故障（熔断计数的唯一口径）：网络错误 / 超时 / <see cref="HttpRequestException"/> /
    /// <see cref="IOException"/> / 5xx 计入；取消、全部 4xx（含 408 / 429）与
    /// <see cref="TotalTimeoutExpiredBeforeSend"/>（请求从未发出）不计入。
    /// </summary>
    public bool IsFailure
    {
        get
        {
            // 有响应：只认 5xx（validateStatus 放行 5xx 时异常为 null）。
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

            // 总预算在请求发出之前就耗尽：本地约束先到期，不是下游不响应。
            // 记成故障的后果是「过载 → 排队 → 预算耗尽」把一个健康的 host 熔断，
            // 把局部排队放大成整条链路不可用 —— 而排队恰恰是 TotalTimeout 最该待命的场景。
            if (TotalTimeoutExpiredBeforeSend)
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
