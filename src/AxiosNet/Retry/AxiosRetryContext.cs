namespace AxiosNet.Retry;

/// <summary>
/// 一次失败尝试的上下文：策略据此判断「要不要重试」与「等多久再试」，
/// 也作为 <see cref="AxiosConfig.RetrySettings.OnRetry"/> 回调的入参。
/// <para>
/// 是只读结构体（栈上传递，不进 GC），自定义策略可以直接按需读字段。
/// </para>
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

        // 状态码在这里就固定下来：泛型入口（GetAsync<T> / 声明式 RequestAsync）会把同一个响应对象
        // 交给后续尝试复用，等回调结束再读 Response.Status 拿到的可能已是「下一次尝试」的值。
        StatusCode = response?.Status;

        Delay = delay;
    }

    /// <summary>
    /// 刚失败的尝试序号，<b>从 1 开始</b>：<c>1</c> 表示首次请求失败（即将进行第 1 次重试）。
    /// <para>退避计算用它做指数：<c>Delay * BackoffFactor^(Attempt-1)</c>。</para>
    /// </summary>
    public int Attempt { get; }

    /// <summary>策略允许的最大重试次数（不含首次请求）；<c>Attempt &gt; MaxRetries</c> 即不再重试。</summary>
    public int MaxRetries { get; }

    /// <summary>本次失败抛出的异常。</summary>
    public Exception Exception { get; }

    /// <summary>本次请求的生效配置（已合并实例默认值、已跑完请求拦截器）。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>
    /// 失败时的响应对象：状态码非法（4xx/5xx）时有值，网络层失败时为 <c>null</c>。
    /// <para>
    /// <b>这是「那一次尝试」的响应实例，不是快照</b>：泛型入口（<c>GetAsync&lt;T&gt;</c> / 声明式
    /// <c>RequestAsync</c>）为省一次分配会把同一个响应对象交给后续尝试复用，因此回调结束后它的
    /// <see cref="AxiosResponse.Status"/> / <see cref="AxiosResponse.Data"/> 可能已被下一次尝试改写。
    /// 要在回调之外保留结论请读 <see cref="StatusCode"/>（构造时快照）与 <see cref="ErrorCode"/>；
    /// 需要响应头（如 <c>Retry-After</c>、业务错误码）请在回调<b>内部</b>读完。
    /// </para>
    /// </summary>
    public AxiosResponse? Response { get; }

    /// <summary>
    /// 本次重试前的等待时间。
    /// <para>
    /// 只有 <see cref="AxiosConfig.RetrySettings.OnRetry"/> 回调里是有效值：
    /// 此时等待时间已经算好，回调可以据此写日志或做埋点。策略的
    /// <see cref="IAxiosRetryPolicy.ShouldRetry"/> 阶段还没计算，读到的总是 <see cref="TimeSpan.Zero"/>。
    /// </para>
    /// </summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// HTTP 状态码（有响应时可用）：<b>构造时的快照</b>，与 <see cref="Response"/> 不同，它不会随下一次尝试改变。
    /// <para>要把失败结论带到回调之外（日志、埋点、聚合统计）就读这个，别回头去读 <c>Response.Status</c>。</para>
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>库内错误码（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / <c>ERR_BAD_RESPONSE</c> 等）；非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode => (Exception as AxiosException)?.Code;

    /// <summary>请求方法（用于幂等判定）。</summary>
    public HttpMethod? Method => Config?.Method;
}
