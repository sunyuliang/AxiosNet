namespace AxiosNet.Telemetry;

/// <summary>
/// <c>AxiosNet.RequestStart</c> 事件的载荷：请求已成型（拦截器与 transformRequest 跑完、URL 已拼好），即将进入发送阶段。
/// <see cref="Config"/> 是本次请求独占的生效配置，事件结束后仍可安全读取。
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

/// <summary>
/// <c>AxiosNet.AttemptStop</c> / <c>AxiosNet.AttemptFailed</c> 事件的载荷：一次尝试（单次发送）的结果。
/// 只有链上装了 <see cref="Pipeline.Attempt.Audit"/> 时才会产生，且只覆盖真正到达终端的尝试
/// （熔断拒绝、闸门排队超时与短路响应都不产生尝试事件）。
/// 对冲的每条分支、备用端点的每个端点各一条事件；<see cref="EndpointIndex"/> 只区分端点，
/// 同一端点下的对冲分支三项序号完全相同，无法靠字段区分。
/// 被胜出者取消的败者不发失败事件。
/// </summary>
public sealed class AxiosAttemptEvent
{
    internal AxiosAttemptEvent(
        int attemptNumber,
        int endpointIndex,
        HttpMethod method,
        Uri uri,
        TimeSpan elapsed,
        int? status,
        string? errorCode,
        Exception? exception)
    {
        AttemptNumber = attemptNumber;
        EndpointIndex = endpointIndex;
        Method = method;
        Uri = uri;
        Elapsed = elapsed;
        Status = status;
        ErrorCode = errorCode;
        Exception = exception;
    }

    /// <summary>第几次尝试（从 1 开始；重试 N 次时最多 N+1 条）。</summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// 第几个端点（从 <c>0</c> 开始）：<c>0</c> = 请求自己的地址，<c>n</c> = 端点表里第 n 个备用端点。
    /// 换端点不推进 <see cref="AttemptNumber"/>，所以同一个尝试号下可能有多条事件，靠这一项区分；
    /// 对冲分支不推进它，同一端点下的分支事件序号相同。
    /// </summary>
    public int EndpointIndex { get; }

    /// <summary>请求方法。</summary>
    public HttpMethod Method { get; }

    /// <summary>本次尝试的请求地址。</summary>
    public Uri Uri { get; }

    /// <summary>
    /// 本次尝试的耗时：审计中间件装在推荐位置（闸门之后）时，从进入终端前到终端返回（流式响应只到响应头），
    /// 不含闸门排队与退避等待，但包含取连接租约与缓冲响应读完正文的时间；装在更外层时会连它内层的耗时一起算。
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>HTTP 状态码（拿到响应时可用）；无响应时为 <c>null</c>。</summary>
    public int? Status { get; }

    /// <summary>库错误码；成功或非 <see cref="AxiosException"/> 时为 <c>null</c>。</summary>
    public string? ErrorCode { get; }

    /// <summary>本次尝试抛出的异常；成功时为 <c>null</c>。</summary>
    public Exception? Exception { get; }
}
