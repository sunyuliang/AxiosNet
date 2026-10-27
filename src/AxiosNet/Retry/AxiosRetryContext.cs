namespace AxiosNet.Retry;

/// <summary>
/// 一次失败尝试的上下文：策略据此判断要不要重试、等多久再试，
/// 也作为 <see cref="AxiosConfig.RetrySettings.OnRetry"/> 回调的入参（只读结构体，栈上传递）。
/// </summary>
public readonly struct AxiosRetryContext
{
    /// <param name="attempt">刚失败的尝试序号，从 1 开始（1 = 首次请求就失败了）。</param>
    /// <param name="maxRetries">策略允许的最大重试次数（不含首次请求）。</param>
    /// <param name="exception">本次失败抛出的异常（库内统一错误模型 <see cref="AxiosException"/>）。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="response">失败时拿到的响应（状态码非法 / 5xx 时可用；网络错误为 <c>null</c>）。</param>
    /// <param name="delay">本次重试前的等待时间（仅 <see cref="AxiosConfig.RetrySettings.OnRetry"/> 回调中有值）。</param>
    public AxiosRetryContext(
        int attempt,
        int maxRetries,
        Exception exception,
        AxiosConfig? config = null,
        AxiosResponse? response = null,
        TimeSpan delay = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Attempt = attempt;
        MaxRetries = maxRetries;
        Exception = exception;
        Config = config;
        Response = response;

        // 状态码在这里就固定下来：响应对象会被后续尝试复用改写。
        StatusCode = response?.Status;

        Delay = delay;
    }

    /// <summary>刚失败的尝试序号，从 1 开始（1 表示首次请求失败，即将进行第 1 次重试）。</summary>
    public int Attempt { get; }

    /// <summary>策略允许的最大重试次数（不含首次请求）；<c>Attempt &gt; MaxRetries</c> 即不再重试。</summary>
    public int MaxRetries { get; }

    /// <summary>本次失败抛出的异常。</summary>
    public Exception Exception { get; }

    /// <summary>本次请求的生效配置（已合并实例默认值、已跑完请求拦截器）。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>
    /// 失败时的响应对象：状态码非法（4xx/5xx）时有值，网络层失败时为 <c>null</c>。
    /// 该实例会被后续尝试复用改写，需要稳定的结论请读 <see cref="StatusCode"/>。
    /// </summary>
    public AxiosResponse? Response { get; }

    /// <summary>本次重试前的等待时间（仅 <see cref="AxiosConfig.RetrySettings.OnRetry"/> 回调中有值）。</summary>
    public TimeSpan Delay { get; }

    /// <summary>HTTP 状态码（有响应时可用），构造时的快照，不会随下一次尝试改变。</summary>
    public int? StatusCode { get; }

    /// <summary>库内错误码（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / <c>ERR_BAD_RESPONSE</c> 等）；非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode => (Exception as AxiosException)?.Code;

    /// <summary>请求方法（用于幂等判定）。</summary>
    public HttpMethod? Method => Config?.Method;
}
