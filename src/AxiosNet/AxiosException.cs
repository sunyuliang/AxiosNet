namespace AxiosNet;

/// <summary>对应 axios 的 AxiosError：携带 code / config / request / response，错误码与 axios 对齐。</summary>
public sealed class AxiosException : Exception
{
    /// <summary>HTTP 状态码非法（默认 validateStatus 不接受 4xx，且非 5xx）。</summary>
    public const string ErrBadRequest = "ERR_BAD_REQUEST";

    /// <summary>服务端 5xx，或响应体无法按目标类型解析、超出 maxContentLength。</summary>
    public const string ErrBadResponse = "ERR_BAD_RESPONSE";

    /// <summary>请求超时（对应 <see cref="AxiosConfig.Timeout"/>）。</summary>
    public const string ErrConnAborted = "ECONNABORTED";

    /// <summary>网络层失败（连接被拒、DNS 失败等）。</summary>
    public const string ErrNetwork = "ERR_NETWORK";

    /// <summary>请求被取消。</summary>
    public const string ErrCanceled = "ERR_CANCELED";

    /// <summary>URL 非法（含相对 URL 但未提供 baseURL）。</summary>
    public const string ErrInvalidUrl = "ERR_INVALID_URL";

    /// <summary>配置项非法或无法按请求生效（例如按请求设置代理）。</summary>
    public const string ErrBadOptionValue = "ERR_BAD_OPTION_VALUE";

    /// <summary>
    /// 并发闸门排队超时：等不到空闲槽位（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/> +
    /// <see cref="AxiosConfig.LimitSettings.QueueTimeout"/>），请求没有发出去。
    /// </summary>
    public const string ErrQueueTimeout = "ERR_QUEUE_TIMEOUT";

    /// <summary>
    /// 并发闸门的排队队列已满（<see cref="AxiosConfig.LimitSettings.MaxQueueLength"/>）：请求<b>没有发出</b>。
    /// 与 <see cref="ErrQueueTimeout"/> 同族但含义不同：不是「等太久了」，而是「连排队的名额都没了」——
    /// 说明调用方的并发压力已经超过本实例的承受能力，应降速 / 换实例，而不是加大排队时间。
    /// </summary>
    public const string ErrQueueFull = "ERR_QUEUE_FULL";

    /// <summary>熔断打开：请求<b>没有发出</b>（<see cref="AxiosConfig.Circuit"/>）。</summary>
    public const string ErrCircuitOpen = "ERR_CIRCUIT_OPEN";

    /// <summary>
    /// 连接池 origin 基数饱和（<see cref="AxiosNet.Pooling.ConnectionPoolOptions.MaxEndpointCount"/>）：
    /// 同时「在途」的不同 host:port 数量达上限，且没有可驱逐的空闲节点。请求根本没发出去，
    /// 与 <see cref="ErrQueueTimeout"/> / <see cref="ErrCircuitOpen"/> 同族——可观测、默认不自动重发、
    /// 调用方应降扇出 / 调大上限 / 退避后重试。
    /// </summary>
    public const string ErrPoolExhausted = "ERR_POOL_EXHAUSTED";

    /// <summary>对应 axios 的 <c>isAxiosError</c>：这个类型本身就是「axios 错误」，恒为 <c>true</c>，供跨语言对照与统一判型。</summary>
    public bool IsAxiosError => true;

    /// <summary>错误码，取值见 <see cref="ErrBadRequest"/> 等常量（与 axios 对齐）；分支判断用它，别解析消息文本。</summary>
    public string? Code { get; }

    /// <summary>触发本次错误的请求配置（该请求的生效值）。想在错误里复盘「当时用了什么参数」看这里。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>触发本次错误的请求。请求结束后 <c>Content</c> 已释放，需要结构化快照请用 <see cref="ToJson"/>。</summary>
    public HttpRequestMessage? Request { get; }

    /// <summary>有响应时可用（5xx、状态码非法、反序列化失败等）；结构性错误（URL 非法、网络失败、取消）为 <c>null</c>。</summary>
    public AxiosResponse? Response { get; }

    /// <summary>HTTP 状态码（有响应时可用，等价于 <c>Response.Status</c>）。</summary>
    public int? Status => Response?.Status;

    /// <summary>熔断拒绝时的建议等待时间（只有 <see cref="ErrCircuitOpen"/> 才可能有值，库不会据此自动重试）。</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>底层异常（例如网络错误的 <see cref="System.Net.Http.HttpRequestException"/>、JSON 解析异常）。</summary>
    public Exception? Cause => InnerException;

    /// <summary>
    /// 是否属于「<see cref="AxiosConfig.TotalTimeout"/> 在请求<b>发出之前</b>就已耗尽」——<c>ECONNABORTED</c> 的一个子集，
    /// 与「发出之后超时」区分开。
    /// </summary>
    /// <remarks>
    /// 「发出之后超时」说明下游不响应，换端点或重试有意义；这条只说明本地预算先到期，下游一个字节都没收到。
    /// 调用方据此应当缩短后续工作 / 降扇出，而不是立刻重试 —— 预算已经空了，重试必然在同一处立刻失败。
    /// 库内部按同一口径分流：不计入熔断失败、不重试、不换端点（见 <see cref="Circuit.AxiosCircuitContext.TotalTimeoutExpiredBeforeSend"/>）。
    /// </remarks>
    public bool IsTotalTimeoutExceeded { get; private init; }

    private AxiosException(
        string message,
        string? code,
        AxiosConfig? config,
        HttpRequestMessage? request,
        AxiosResponse? response,
        Exception? innerException,
        TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        Code = code;
        Config = config;
        Request = request;
        Response = response;
        RetryAfter = retryAfter;
    }

    /// <summary>对应 axios <c>error.toJSON()</c>：可直接序列化进日志的结构化快照。</summary>
    public IReadOnlyDictionary<string, object?> ToJson()
    {
        var request = Request;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "AxiosError",
            ["message"] = Message,
            ["code"] = Code,
            ["status"] = Status,
            ["method"] = request?.Method.Method,
            ["url"] = request?.RequestUri?.ToString(),
            ["isAxiosError"] = true,
            ["stack"] = StackTrace
        };
    }

    /// <summary>
    /// 单次尝试超时。<paramref name="effectiveTimeout"/> 只在「被总预算压小」时传入：
    /// 那时真实上限已经不是 <see cref="AxiosConfig.Timeout"/>，消息里必须说清差额，否则调用方会以为配置没生效。
    /// </summary>
    internal static AxiosException Timeout(
        AxiosConfig config,
        HttpRequestMessage request,
        Exception? inner = null,
        TimeSpan? effectiveTimeout = null)
    {
        string message;

        if (!string.IsNullOrWhiteSpace(config.CallbacksOrNull?.TimeoutMessage))
        {
            message = config.CallbacksOrNull!.TimeoutMessage!;
        }
        else if (effectiveTimeout is { } effective)
        {
            // 「没设 Timeout」不能写成 0ms：0 在库里的含义是「不限时」，写成 0ms 会被读成「给了一个极小的上限」。
            var configured = config.Timeout > TimeSpan.Zero
                ? $"{config.Timeout.TotalMilliseconds:0}ms"
                : "unset";

            message = $"timeout of {effective.TotalMilliseconds:0}ms exceeded "
                      + $"(remaining TotalTimeout budget; Timeout = {configured})";
        }
        else
        {
            message = $"timeout of {config.Timeout.TotalMilliseconds:0}ms exceeded";
        }

        return new AxiosException(message, ErrConnAborted, config, request, null, inner);
    }

    /// <summary>
    /// 总预算（<see cref="AxiosConfig.TotalTimeout"/>）已耗尽，本次尝试<b>根本没有发出</b>：错误码与超时一致（<c>ECONNABORTED</c>），
    /// 消息指明是第几次尝试、总预算多少。
    /// </summary>
    /// <param name="config">本次请求的生效配置（错误码、回调文案与超时消息都取自它）。</param>
    /// <param name="request">触发本次失败的请求（挂在异常上供调用方定位）。</param>
    /// <param name="attempt">第几次尝试未能发出，写进消息里便于排查。</param>
    /// <param name="budget">
    /// 总预算的配置值，只用于消息。取 <see cref="AxiosConfig.TotalTimeout"/> 原样（可空）而不是强制解包：
    /// 截止点由入口处的时间戳唯一决定（<c>TotalTimeoutBudget</c>），但配置对象是同一个实例，管线跑起来之后
    /// 仍可能被自定义中间件 / 回调改写成 <c>null</c>。那种情况下这里只该少写一个金额，
    /// 不该把「预算耗尽」变成 <c>InvalidOperationException</c>。
    /// </param>
    /// <param name="inner">把预算耗尽归因到的那次失败（通常是上一次尝试抛出的异常），保留 <c>Cause</c> 链。</param>
    internal static AxiosException TotalTimeoutExceeded(
        AxiosConfig config,
        HttpRequestMessage? request,
        int attempt,
        TimeSpan? budget,
        Exception? inner = null)
    {
        var headline = budget is { } known && known > TimeSpan.Zero
            ? $"TotalTimeout of {known.TotalMilliseconds:0}ms exceeded"
            : "TotalTimeout exceeded";

        var message = string.IsNullOrWhiteSpace(config.CallbacksOrNull?.TimeoutMessage)
            ? $"{headline} before attempt #{attempt} could be sent: "
              + "the total budget covers all retries, failover endpoints and hedges — "
              + "raise TotalTimeout, cut MaxRetries / MaxHedges, or shorten Timeout"
            : config.CallbacksOrNull!.TimeoutMessage!;

        return new AxiosException(message, ErrConnAborted, config, request, null, inner)
        {
            IsTotalTimeoutExceeded = true
        };
    }

    /// <summary>流式响应的单帧超过内部上限（没有分帧符的畸形流会把缓冲区撑到不可控）。</summary>
    internal static AxiosException StreamFrameTooLarge(AxiosConfig config, HttpRequestMessage? request, string format, int limit)
    {
        return new AxiosException(
            $"{format} frame exceeded the {limit} byte limit: the stream is missing its frame delimiter",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>JSON 数组流里的单个元素超过上限（元素必须能整块交给序列化器）。</summary>
    internal static AxiosException StreamElementTooLarge(AxiosConfig config, HttpRequestMessage? request, string format, int limit)
    {
        return new AxiosException(
            $"{format} element exceeded the {limit} byte limit: one element must fit in a single frame",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>流式分帧层面的协议错误（顶层不是数组 / JSON 被截断 / 字节不是合法 JSON）。</summary>
    internal static AxiosException StreamMalformed(AxiosConfig config, HttpRequestMessage? request, string detail, Exception? inner = null)
    {
        return new AxiosException(
            $"Invalid streamed response: {detail}",
            ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    /// <summary>并发闸门排队超时：在 <see cref="AxiosConfig.LimitSettings.QueueTimeout"/> 之内没等到空闲槽位。</summary>
    internal static AxiosException QueueTimeout(AxiosConfig config, HttpRequestMessage? request, TimeSpan waited, int maxConcurrency, Exception? inner = null)
    {
        return new AxiosException(
            $"no free concurrency slot within {waited.TotalMilliseconds:0}ms "
            + $"(MaxConcurrentRequests = {maxConcurrency}): "
            + "reduce parallelism, raise Limits.MaxConcurrentRequests / Limits.QueueTimeout, "
            + "or back off and retry on ERR_QUEUE_TIMEOUT",
            ErrQueueTimeout,
            config,
            request,
            null,
            inner);
    }

    /// <summary>并发闸门的排队队列已满：超过 <c>Limits.MaxQueueLength</c>，请求没有发出。</summary>
    internal static AxiosException QueueFull(AxiosConfig config, HttpRequestMessage? request, int maxQueueLength, int maxConcurrency)
    {
        return new AxiosException(
            $"concurrency queue is full (MaxQueueLength = {maxQueueLength}, MaxConcurrentRequests = {maxConcurrency}): "
            + "reduce parallel callers, raise Limits.MaxQueueLength / Limits.MaxConcurrentRequests, "
            + "or back off and retry on ERR_QUEUE_FULL",
            ErrQueueFull,
            config,
            request,
            null,
            null);
    }

    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request, Exception? inner = null)
    {
        return new AxiosException("Request canceled", ErrCanceled, config, request, null, inner);
    }

    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request, string message, Exception? inner = null)
    {
        return new AxiosException(message, ErrCanceled, config, request, null, inner);
    }

    internal static AxiosException Network(AxiosConfig config, HttpRequestMessage? request, Exception inner)
    {
        return new AxiosException("Network Error", ErrNetwork, config, request, null, inner);
    }

    /// <summary>熔断拒绝：请求没有发出（没有连接、没有流量、没有副作用），消息里带上计数维度与建议等待时间。</summary>
    internal static AxiosException CircuitOpen(AxiosConfig config, HttpRequestMessage? request, string host, TimeSpan retryAfter)
    {
        var hint = retryAfter > TimeSpan.Zero
            ? $" (retry after {FormatWait(retryAfter)})"
            : string.Empty;

        return new AxiosException(
            $"Circuit breaker is open for {host}: the request was not sent{hint}",
            ErrCircuitOpen,
            config,
            request,
            null,
            null,
            retryAfter > TimeSpan.Zero ? retryAfter : null);
    }

    /// <summary>
    /// 连接池 origin 基数饱和：distinct origin 同时「在途」的数量触顶 <see cref="AxiosNet.Pooling.ConnectionPoolOptions.MaxEndpointCount"/>，
    /// 且没有可驱逐的空闲节点。请求没有发出去；与 <see cref="ErrQueueTimeout"/> / <see cref="ErrCircuitOpen"/> 同族，
    /// 默认不自动重发。调用方应调大 <c>MaxEndpointCount</c>、用 <c>Limits.MaxConcurrentRequests</c> +
    /// <c>Limits.QueueTimeout</c> 收扇出、显式 <c>Clear()</c> 回收，或退避后重试。
    /// </summary>
    internal static AxiosException PoolExhausted(AxiosConfig? config, HttpRequestMessage? request, int max, Exception? inner = null)
    {
        return new AxiosException(
            $"connection pool origin capacity exhausted (MaxEndpointCount = {max}): no idle node to evict "
            + "and all nodes are in use. Raise ConnectionPoolOptions.MaxEndpointCount, "
            + "cap fan-out with Limits.MaxConcurrentRequests / Limits.QueueTimeout, call Clear() to reclaim, "
            + "or back off and retry on ERR_POOL_EXHAUSTED",
            ErrPoolExhausted,
            config,
            request,
            null,
            inner);
    }

    /// <summary>等待时长的可读形式：不足 1 秒用毫秒。</summary>
    private static string FormatWait(TimeSpan value)
        => value >= TimeSpan.FromSeconds(1)
            ? $"{value.TotalSeconds:0.##}s"
            : $"{value.TotalMilliseconds:0}ms";

    internal static AxiosException InvalidUrl(string url, AxiosConfig? config = null, Exception? inner = null, string? message = null)
    {
        // 超长 URL 本身就会让 Uri 拒绝解析，整条塞进消息只会变成几十万字符。
        return new AxiosException(message ?? $"Invalid URL: {Truncate(url)}", ErrInvalidUrl, config, null, null, inner);
    }

    private const int MaxUrlInMessage = 512;

    private static string Truncate(string value)
        => value.Length <= MaxUrlInMessage
            ? value
            : value[..MaxUrlInMessage] + $"...(truncated, {value.Length} chars)";

    internal static AxiosException InvalidOption(string message, AxiosConfig? config = null)
        => InvalidOption(message, config, null);

    internal static AxiosException InvalidOption(string message, AxiosConfig? config, Exception? inner)
    {
        return new AxiosException(message, ErrBadOptionValue, config, null, null, inner);
    }

    internal static AxiosException FromResponse(AxiosConfig config, HttpRequestMessage? request, AxiosResponse response)
    {
        var code = response.Status >= 500 ? ErrBadResponse : ErrBadRequest;
        return new AxiosException(
            $"Request failed with status code {response.Status}",
            code,
            config,
            request,
            response,
            null);
    }

    internal static AxiosException BadResponse(AxiosConfig? config, HttpRequestMessage? request, Type targetType, Exception inner)
        => BadResponse(config, request, targetType.Name, inner);

    internal static AxiosException BadResponse(AxiosConfig? config, HttpRequestMessage? request, string targetName, Exception inner)
    {
        return new AxiosException(
            $"Response body could not be deserialized to '{targetName}'",
            ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    /// <summary>响应反序列化出的枚举值不在已定义集合内。</summary>
    internal static AxiosException UndefinedEnumValue(AxiosConfig? config, HttpRequestMessage? request, string targetName, string value, Exception? inner = null)
    {
        return new AxiosException(
            $"Response body deserialized to an undefined '{targetName}' value: {value}",
            ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    internal static AxiosException BadRequestPayload(AxiosConfig? config, HttpRequestMessage? request, string targetName, Exception inner)
    {
        return new AxiosException(
            $"Request body could not be serialized from '{targetName}'",
            ErrBadRequest,
            config,
            request,
            null,
            inner);
    }

    /// <summary>响应侧 <c>transformResponse</c> 抛错，原始异常在 <see cref="Cause"/> 里。</summary>
    internal static AxiosException ResponseTransformFailed(AxiosConfig? config, HttpRequestMessage? request, Exception inner)
    {
        return new AxiosException(
            "Response data could not be transformed by transformResponse",
            ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    /// <summary>
    /// 回调抛出异常（回调属于请求管线，异常不能以裸类型漏给调用方）；上传阶段取
    /// <c>ERR_BAD_REQUEST</c>、下载 / 原始字节阶段取 <c>ERR_BAD_RESPONSE</c>，原始异常在 <see cref="Cause"/> 里。
    /// </summary>
    internal static AxiosException CallbackFailed(AxiosConfig config, HttpRequestMessage? request, string callbackName, Exception inner, bool upload)
    {
        return new AxiosException(
            $"{callbackName} 回调抛出异常，请求已中断：{inner.Message}",
            upload ? ErrBadRequest : ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    /// <summary>响应体超过 <c>Limits.MaxContentLength</c>；<paramref name="request"/> 允许为 <c>null</c>（短路响应）。</summary>
    internal static AxiosException MaxContentLength(AxiosConfig config, HttpRequestMessage? request, long actual, long limit)
    {
        return new AxiosException(
            $"maxContentLength size of {limit} exceeded (got {actual} bytes)",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>响应体超过单数组上限（<see cref="Array.MaxLength"/>，约 2GB），无法装进 <c>Response.Data</c>。</summary>
    internal static AxiosException ResponseTooLarge(AxiosConfig config, HttpRequestMessage request, long actual)
    {
        return new AxiosException(
            $"Response body of {actual} bytes exceeds the maximum array length ({Array.MaxLength}): "
            + "Response.Data cannot hold it as a single byte[]. "
            + "Set MaxContentLength to fail earlier, or use ResponseType.Stream to read it incrementally.",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    internal static AxiosException MaxBodyLength(AxiosConfig config, HttpRequestMessage request, long actual, long limit)
    {
        return new AxiosException(
            $"Request body larger than maxBodyLength limit ({actual} > {limit} bytes)",
            ErrBadRequest,
            config,
            request,
            null,
            null);
    }
}
