namespace AxiosNet.Telemetry;

/// <summary>
/// <c>AxiosNet.RequestStart</c> 事件的载荷：请求已成型（拦截器与 transformRequest 跑完、URL 已拼好），
/// 即将进入发送阶段。
/// <para>
/// 与 <c>HttpClient</c> 的 <c>RequestStart</c> 同一时机，但带的是**库的配置**：订阅方可以直接读
/// <see cref="Config"/> 里的头 / 超时 / 重试策略，不必再去猜哪一层改过它。
/// </para>
/// <para>载荷是快照：<see cref="Config"/> 是本次请求独占的生效配置，事件结束后仍可安全读取。</para>
/// </summary>
public sealed class AxiosRequestStartEvent
{
    internal AxiosRequestStartEvent(HttpMethod method, Uri uri, AxiosConfig config)
    {
        Method = method;
        Uri = uri;
        Config = config;
    }

    /// <summary>请求方法（拦截器可能改过，这里是最终值）。</summary>
    public HttpMethod Method { get; }

    /// <summary>最终请求地址（已含 baseURL 合并与 params 序列化）。</summary>
    public Uri Uri { get; }

    /// <summary>本次请求的生效配置。</summary>
    public AxiosConfig Config { get; }
}

/// <summary>
/// <c>AxiosNet.RequestStop</c> 事件的载荷：请求成功结束（含 <see cref="AxiosConfig.CallbackSettings.ValidateStatus"/>
/// 接受的非 2xx 状态）。
/// </summary>
public sealed class AxiosRequestStopEvent
{
    internal AxiosRequestStopEvent(HttpMethod method, Uri uri, AxiosResponse response, TimeSpan elapsed)
    {
        Method = method;
        Uri = uri;
        Response = response;
        Elapsed = elapsed;
    }

    /// <summary>请求方法。</summary>
    public HttpMethod Method { get; }

    /// <summary>请求地址。</summary>
    public Uri Uri { get; }

    /// <summary>响应（含状态码、响应头、已转换的 <see cref="AxiosResponse.Data"/>）。</summary>
    public AxiosResponse Response { get; }

    /// <summary>本次逻辑请求的总耗时（含重试与退避等待；流式响应只到响应头）。</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>
/// <c>AxiosNet.RequestFailed</c> 事件的载荷：请求以异常结束。
/// </summary>
public sealed class AxiosRequestFailedEvent
{
    internal AxiosRequestFailedEvent(
        HttpMethod method,
        Uri uri,
        Exception exception,
        string? errorCode,
        int? status,
        TimeSpan elapsed)
    {
        Method = method;
        Uri = uri;
        Exception = exception;
        ErrorCode = errorCode;
        Status = status;
        Elapsed = elapsed;
    }

    /// <summary>请求方法。</summary>
    public HttpMethod Method { get; }

    /// <summary>请求地址。</summary>
    public Uri Uri { get; }

    /// <summary>抛出的异常（库内统一是 <see cref="AxiosException"/>）。</summary>
    public Exception Exception { get; }

    /// <summary>库错误码（<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / <c>ERR_BAD_RESPONSE</c> …）；非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode { get; }

    /// <summary>HTTP 状态码（有响应时可用）。</summary>
    public int? Status { get; }

    /// <summary>本次逻辑请求的总耗时（含重试与退避等待）。</summary>
    public TimeSpan Elapsed { get; }
}
