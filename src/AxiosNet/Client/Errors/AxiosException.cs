namespace AxiosNet;

/// <summary>
/// 对应 axios 的 AxiosError：携带 code / config / request / response，错误码与 axios 对齐。
/// </summary>
/// <remarks>
/// 请求失败一律是本类型；调用方传参非法（头名不是合法 token、用户名含冒号、<c>ContentType</c> 为空白等）抛 BCL 的 <see cref="ArgumentException"/>。
/// 状态码被拒绝时消息附服务端正文片段（最多 512 字符），完整原文见 <see cref="ResponseBody"/>。
/// </remarks>
public class AxiosException : Exception
{
    /// <summary>HTTP 状态码非法且非 5xx（默认 validateStatus 下即 4xx），或请求阶段就失败（载荷序列化、上传 / 发送前回调）。</summary>
    public const string ErrBadRequest = "ERR_BAD_REQUEST";

    /// <summary>服务端 5xx，或响应体无法按目标类型解析、超出 maxContentLength。</summary>
    public const string ErrBadResponse = "ERR_BAD_RESPONSE";

    /// <summary>请求超时（<see cref="AxiosConfig.Timeout"/>）；总预算在发送前耗尽时也用它，靠 <see cref="IsTotalTimeoutExceeded"/> 区分。</summary>
    public const string ErrConnAborted = "ECONNABORTED";

    /// <summary>网络层失败（连接被拒、DNS 失败等）。</summary>
    public const string ErrNetwork = "ERR_NETWORK";

    /// <summary>请求被取消。</summary>
    public const string ErrCanceled = "ERR_CANCELED";

    /// <summary>URL 非法（含相对 URL 但未提供 baseURL）。</summary>
    public const string ErrInvalidUrl = "ERR_INVALID_URL";

    /// <summary>配置项非法或无法按请求生效（例如按请求设置代理）。</summary>
    public const string ErrBadOptionValue = "ERR_BAD_OPTION_VALUE";

    /// <summary>并发闸门排队超时：等不到空闲槽位，请求没有发出去。</summary>
    public const string ErrQueueTimeout = "ERR_QUEUE_TIMEOUT";

    /// <summary>并发闸门的排队队列已满（<c>AxiosClientOptions.MaxQueue</c>）：请求没有发出。</summary>
    public const string ErrQueueFull = "ERR_QUEUE_FULL";

    /// <summary>熔断打开：请求<b>没有发出</b>（<see cref="AxiosConfig.ResilienceSettings.Circuit"/>）。</summary>
    public const string ErrCircuitOpen = "ERR_CIRCUIT_OPEN";

    /// <summary>连接池 origin 基数饱和（<see cref="AxiosNet.Pooling.ConnectionPoolOptions.MaxEndpointCount"/>）：请求没有发出。</summary>
    public const string ErrPoolExhausted = "ERR_POOL_EXHAUSTED";

    /// <summary>
    /// 重定向次数用尽：服务端仍返回 3xx + <c>Location</c>，已跟随到 <c>AxiosClientOptions.MaxRedirects</c> 上限。
    /// 与 <c>MaxRedirects = 0</c>（关闭跟随，报 <see cref="ErrBadRequest"/>）用错误码区分。
    /// </summary>
    public const string ErrTooManyRedirects = "ERR_TOO_MANY_REDIRECTS";

    /// <summary>对应 axios 的 <c>isAxiosError</c>：恒为 <c>true</c>。</summary>
    public bool IsAxiosError => true;

    /// <summary>错误码，取值见 <see cref="ErrBadRequest"/> 等常量；分支判断用它，别解析消息文本。</summary>
    public string? Code { get; }

    /// <summary>
    /// <see cref="Code"/> 的枚举形式：值相同时可直接 <c>switch</c> 穷举；不是内置常量时为 <see cref="AxiosErrorCode.Unknown"/>。
    /// </summary>
    public AxiosErrorCode ErrorCode => AxiosErrorCodes.FromString(Code);

    /// <summary>触发本次错误的请求配置（该请求的生效值）。</summary>
    public AxiosConfig? Config { get; }

    /// <summary>触发本次错误的请求。请求结束后 <c>Content</c> 已释放，需要结构化快照请用 <see cref="ToJson"/>。</summary>
    public HttpRequestMessage? Request { get; }

    /// <summary>有响应时可用（5xx、状态码非法、反序列化失败等）；结构性错误（URL 非法、网络失败、取消）为 <c>null</c>。</summary>
    public AxiosResponse? Response { get; internal set; }

    /// <summary>HTTP 状态码（有响应时可用，等价于 <c>Response.Status</c>）。</summary>
    public int? Status => Response?.Status;

    /// <summary>失败响应的正文原文（等价于 <c>Response.RawText</c>）。</summary>
    /// <remarks>状态码被拒绝或响应装配失败时保留；网络流式成功响应没有正文，为 <c>null</c>。</remarks>
    public string? ResponseBody => Response?.RawText;

    /// <summary>熔断拒绝时的建议等待时间（只有 <see cref="ErrCircuitOpen"/> 才可能有值，库不会据此自动重试）。</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>底层异常（例如网络错误的 <see cref="System.Net.Http.HttpRequestException"/>、JSON 解析异常）。</summary>
    public Exception? Cause => InnerException;

    /// <summary>
    /// 是否为「整条请求的总预算已花完」：<b>发出前</b>就发现预算耗尽，或本次尝试的上限被剩余预算压小后到点。
    /// </summary>
    /// <remarks>
    /// <c>true</c> 时换端点 / 重试都不会有预算可用，调用方应直接放弃；<c>false</c> 是普通超时（上限就是 <see cref="AxiosConfig.Timeout"/>），
    /// 下游不响应时换端点仍可能有意义。两者错误码都是 <c>ECONNABORTED</c>。
    /// </remarks>
    public bool IsTotalTimeoutExceeded => TotalTimeoutExceededBeforeSend || TimedOutOnTotalBudget;

    /// <summary>本次失败是否「预算在请求发出之前就已耗尽」（排队 / 入口处判定）。</summary>
    /// <remarks>熔断计数据此区分「一次都没发出」与「发出后在途超时」：后者仍是下游故障。</remarks>
    internal bool TotalTimeoutExceededBeforeSend { get; private init; }

    /// <summary>本次超时的上限取自剩余总预算（被 <see cref="AxiosConfig.ResilienceSettings.TotalTimeout"/> 压小）。</summary>
    /// <remarks>
    /// 请求<b>已发出</b>、上限被剩余预算压小后到点，因此仍算一次下游故障（计熔断）；对外与「发出前耗尽」同报
    /// <see cref="IsTotalTimeoutExceeded"/>，区别只在文案与是否换了端点。
    /// </remarks>
    internal bool TimedOutOnTotalBudget { get; private init; }

    /// <summary>
    /// 是否属于「请求构造阶段失败」（默认查询串展平时的取值 / 格式化抛错）。
    /// 核心直调保持 <c>ERR_BAD_OPTION_VALUE</c>，外层可以据此改判自己的错误码。
    /// </summary>
    public bool IsRequestConstructionFailure { get; private init; }

    /// <summary>
    /// 供派生类型构造异常；调用方应当读取库抛出的实例，而不是自己创建。<paramref name="code"/> 取值请用 <see cref="ErrBadRequest"/> 等常量。
    /// </summary>
    protected AxiosException(
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
    /// <remarks>比 axios 多一项 <c>responseBody</c>：失败响应的正文片段（最多 512 字符）。</remarks>
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
            ["responseBody"] = ResponseBody is { } body ? BodySnippet(body) : null,
            ["isAxiosError"] = true,
            ["stack"] = StackTrace
        };
    }

    /// <summary>单次尝试超时。<paramref name="effectiveTimeout"/> 只在被总预算压小时传入，用于在消息里写明真实上限。</summary>
    internal static AxiosException Timeout(
        AxiosConfig config,
        HttpRequestMessage request,
        Exception? inner = null,
        TimeSpan? effectiveTimeout = null)
    {
        var message = CustomTimeoutMessage(config);

        if (message is null)
        {
            if (effectiveTimeout is { } effective)
            {
                // 0 在库里的含义是「不限时」，消息里写 unset。
                var configured = config.Timeout > TimeSpan.Zero
                    ? $"{config.Timeout.TotalMilliseconds:0}ms"
                    : "unset";

                message = $"timeout of {effective.TotalMilliseconds:0}ms exceeded "
                          + $"(remaining TotalTimeout budget; Timeout = {configured}): "
                          + "the total budget is spent, no further attempt is sent";
            }
            else
            {
                message = $"timeout of {config.Timeout.TotalMilliseconds:0}ms exceeded";
            }
        }

        return new AxiosException(message, ErrConnAborted, config, request, null, inner)
        {
            TimedOutOnTotalBudget = effectiveTimeout is not null
        };
    }

    /// <summary>
    /// 总预算已耗尽，本次尝试<b>根本没有发出</b>：错误码与超时一致（<c>ECONNABORTED</c>），消息指明第几次尝试与总预算。
    /// </summary>
    /// <param name="config">本次请求的生效配置（错误码、回调文案与超时消息都取自它）。</param>
    /// <param name="request">触发本次失败的请求。</param>
    /// <param name="attempt">第几次尝试未能发出。</param>
    /// <param name="budget">总预算的配置值，只用于消息。</param>
    /// <param name="inner">预算耗尽归因到的那次失败，保留 <c>Cause</c> 链。</param>
    internal static AxiosException TotalTimeoutExceeded(
        AxiosConfig config,
        HttpRequestMessage? request,
        int attempt,
        TimeSpan? budget,
        Exception? inner = null)
    {
        var message = CustomTimeoutMessage(config);

        if (message is null)
        {
            var headline = budget is { } known && known > TimeSpan.Zero
                ? $"TotalTimeout of {known.TotalMilliseconds:0}ms exceeded"
                : "TotalTimeout exceeded";

            message = $"{headline} before attempt #{attempt} could be sent: "
                      + "the total budget covers all retries, failover endpoints and hedges — "
                      + "raise TotalTimeout, cut MaxRetries / MaxHedges, or shorten Timeout";
        }

        return new AxiosException(message, ErrConnAborted, config, request, null, inner)
        {
            TotalTimeoutExceededBeforeSend = true
        };
    }

    /// <summary>配置里的自定义超时文案；未配置（或只有空白）时返回 <c>null</c>，由调用方拼默认文案。</summary>
    private static string? CustomTimeoutMessage(AxiosConfig config)
        => string.IsNullOrWhiteSpace(config.ObservabilityOrNull?.TimeoutMessage)
            ? null
            : config.ObservabilityOrNull!.TimeoutMessage!;

    /// <summary>流式响应的单帧超过内部上限。</summary>
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

    /// <summary>并发闸门排队超时：在 <c>Resilience.Concurrency.QueueTimeout</c> 之内没等到空闲槽位。</summary>
    internal static AxiosException QueueTimeout(AxiosConfig config, HttpRequestMessage? request, TimeSpan waited, int maxConcurrency, Exception? inner = null)
    {
        return new AxiosException(
            $"no free concurrency slot within {waited.TotalMilliseconds:0}ms "
            + $"(AxiosClientOptions.MaxInFlight = {maxConcurrency}): "
            + "reduce parallelism, raise AxiosClientOptions.MaxInFlight / Resilience.Concurrency.QueueTimeout, "
            + "or back off and retry on ERR_QUEUE_TIMEOUT",
            ErrQueueTimeout,
            config,
            request,
            null,
            inner);
    }

    /// <summary>并发闸门的排队队列已满：超过 <c>AxiosClientOptions.MaxQueue</c>，请求没有发出。</summary>
    internal static AxiosException QueueFull(AxiosConfig config, HttpRequestMessage? request, int maxQueue, int maxConcurrency)
    {
        return new AxiosException(
            $"concurrency queue is full (AxiosClientOptions.MaxQueue = {maxQueue}, MaxInFlight = {maxConcurrency}): "
            + "reduce parallel callers, raise AxiosClientOptions.MaxQueue / AxiosClientOptions.MaxInFlight, "
            + "or back off and retry on ERR_QUEUE_FULL",
            ErrQueueFull,
            config,
            request,
            null,
            null);
    }

    /// <summary>取消（<c>ERR_CANCELED</c>）：默认文案。</summary>
    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request, Exception? inner = null)
    {
        return new AxiosException("Request canceled", ErrCanceled, config, request, null, inner);
    }

    /// <summary>取消（<c>ERR_CANCELED</c>）：带调用方原因，超时与主动取消共用此码。</summary>
    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request, string message, Exception? inner = null)
    {
        return new AxiosException(message, ErrCanceled, config, request, null, inner);
    }

    /// <summary>网络层失败（<c>ERR_NETWORK</c>）：连接 / DNS / TLS 等，原始异常进 <c>Cause</c>。</summary>
    internal static AxiosException Network(AxiosConfig config, HttpRequestMessage? request, Exception inner)
    {
        return new AxiosException("Network Error", ErrNetwork, config, request, null, inner);
    }

    /// <summary>熔断拒绝：请求没有发出，消息带上计数维度与建议等待时间。</summary>
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
    /// 连接池 origin 基数饱和：distinct origin 同时「在途」的数量触顶，且没有可驱逐的空闲节点，请求没有发出。
    /// </summary>
    internal static AxiosException PoolExhausted(AxiosConfig? config, HttpRequestMessage? request, int max, Exception? inner = null)
    {
        return new AxiosException(
            $"connection pool origin capacity exhausted (MaxEndpointCount = {max}): no idle node to evict "
            + "and all nodes are in use. Raise ConnectionPoolOptions.MaxEndpointCount, "
            + "cap fan-out with AxiosClientOptions.MaxInFlight / Resilience.Concurrency.QueueTimeout, call Clear() to reclaim, "
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

    /// <summary>URL 非法（<c>ERR_INVALID_URL</c>）：消息里的 URL 截断到 <c>MaxUrlInMessage</c>。</summary>
    internal static AxiosException InvalidUrl(string url, AxiosConfig? config = null, Exception? inner = null, string? message = null)
    {
        return new AxiosException(message ?? $"Invalid URL: {Truncate(url)}", ErrInvalidUrl, config, null, null, inner);
    }

    // 消息里 URL 的截断长度。
    private const int MaxUrlInMessage = 512;

    /// <summary>超长 URL 截断后再进消息（512 字符上限），尾部标出原始长度。</summary>
    private static string Truncate(string value)
        => value.Length <= MaxUrlInMessage
            ? value
            : value[..MaxUrlInMessage] + $"...(truncated, {value.Length} chars)";

    // 异常消息 / toJSON 里响应正文的字符上限。
    private const int MaxBodyInMessage = 512;

    /// <summary>消息尾部追加正文片段：空正文不追加（保持原文案），换行压平，超长截断。</summary>
    private static string BodySuffix(string? body)
        => string.IsNullOrWhiteSpace(body) ? string.Empty : ": " + BodySnippet(body);

    /// <summary>
    /// 正文片段：去首尾空白、把 CR/LF 折成空格（多行日志不会被拆散），超长时截断并标出原始长度。
    /// 只折叠换行，不改写其余空白。
    /// </summary>
    private static string BodySnippet(string body)
    {
        var text = body.Trim();

        if (text.Contains('\n') || text.Contains('\r'))
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                builder.Append(c is '\r' or '\n' ? ' ' : c);
            }

            text = builder.ToString().Trim();
        }

        return text.Length <= MaxBodyInMessage
            ? text
            : text[..MaxBodyInMessage] + $"...(truncated, {text.Length} chars)";
    }

    /// <summary>选项值非法（<c>ERR_BAD_OPTION_VALUE</c>）：消息直接来自校验点。</summary>
    internal static AxiosException InvalidOption(string message, AxiosConfig? config = null)
        => InvalidOption(message, config, null);

    /// <summary>选项值非法，并保留原始异常。</summary>
    internal static AxiosException InvalidOption(string message, AxiosConfig? config, Exception? inner)
    {
        return new AxiosException(message, ErrBadOptionValue, config, null, null, inner);
    }

    /// <summary>正文为空但调用方按「一定有正文」取值（<c>AxiosResponse&lt;T&gt;.DataOrThrow()</c> 用）。</summary>
    /// <param name="config">本次请求配置。</param>
    /// <param name="request">请求对象（可能为 <c>null</c>）。</param>
    /// <param name="response">本次响应。</param>
    /// <param name="targetType">期望的正文类型名。</param>
    /// <returns>统一的 <c>ERR_BAD_RESPONSE</c>。</returns>
    internal static AxiosException EmptyBody(
        AxiosConfig? config,
        HttpRequestMessage? request,
        AxiosResponse? response,
        string targetType)
        => new(
            $"响应正文为空，不能按 {targetType} 取值：服务端给了成功状态码但没有正文（204 No Content 是常见原因）。"
            + "若空正文是合法结果，请改用 Data 并处理 null。",
            ErrBadResponse,
            config,
            request,
            response,
            innerException: null);

    /// <summary>状态码错误消息里的「方法 + URL（已剥 query / fragment / userinfo）+ 第几次尝试」；拿不到 request 时返回空串。</summary>
    private static string RequestSuffix(HttpRequestMessage? request, int? attemptNumber)
    {
        if (request is null)
        {
            return string.Empty;
        }

        var method = request.Method.Method;
        var location = request.RequestUri is { } uri
            ? method + " " + Truncate(Sanitize(uri))
            : method;

        return attemptNumber is { } attempt
            ? $" ({location}, attempt {attempt})"
            : $" ({location})";
    }

    /// <summary>只保留 scheme + host + port + path，剥掉 query / fragment / userinfo。</summary>
    /// <remarks>路径本身不脱敏：把凭据放进 path 的接口，消息里仍会原样出现该段。</remarks>
    private static string Sanitize(Uri uri)
        => uri.IsAbsoluteUri
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : uri.OriginalString;

    /// <summary>
    /// 非 2xx 响应：5xx 归 <c>ERR_BAD_RESPONSE</c>，4xx 归 <c>ERR_BAD_REQUEST</c>；3xx 且带 <c>Location</c> 时按重定向细分。
    /// 各分支都在状态码前缀后追加「方法 + URL + 第几次尝试」。
    /// </summary>
    /// <param name="config">本次请求的生效配置（错误码取自它）。</param>
    /// <param name="request">触发本次失败的请求；为 <c>null</c> 时不追加上下文后缀。</param>
    /// <param name="response">非 2xx 的响应。</param>
    /// <param name="maxRedirects">实例生效的重定向上限（<c>0</c> = 不跟随），只用于写消息，不参与判定。</param>
    /// <param name="attemptNumber">第几次尝试（从 1 起）；<c>null</c> 时消息不带尝试序号。</param>
    internal static AxiosException FromResponse(
        AxiosConfig config,
        HttpRequestMessage? request,
        AxiosResponse response,
        int maxRedirects,
        int? attemptNumber = null)
    {
        // 3xx + Location：次数到顶报 ERR_TOO_MANY_REDIRECTS，MaxRedirects = 0 报 ERR_BAD_REQUEST。
        if (IsRedirectStatus(response.Status) && HasLocation(response))
        {
            if (maxRedirects == 0)
            {
                return new AxiosException(
                    $"Request failed with status code {response.Status}{RequestSuffix(request, attemptNumber)}: redirects are disabled (MaxRedirects = 0); "
                    + "set AxiosClientOptions.MaxRedirects > 0 to follow redirects",
                    ErrBadRequest,
                    config,
                    request,
                    response,
                    null);
            }

            var limit = maxRedirects;
            return new AxiosException(
                $"Request failed with status code {response.Status}{RequestSuffix(request, attemptNumber)}: too many redirects "
                + $"(stopped after MaxRedirects = {limit}); raise AxiosClientOptions.MaxRedirects, or set it to 0 to stop following",
                ErrTooManyRedirects,
                config,
                request,
                response,
                null);
        }

        var code = response.Status >= 500 ? ErrBadResponse : ErrBadRequest;

        // 消息里带服务端正文片段；重定向分支不带。
        return new AxiosException(
            $"Request failed with status code {response.Status}{RequestSuffix(request, attemptNumber)}{BodySuffix(response.RawText)}",
            code,
            config,
            request,
            response,
            null);
    }

    /// <summary>可跟随的重定向状态码（RFC 9110 §15.4 里的 301 / 302 / 303 / 307 / 308）。</summary>
    private static bool IsRedirectStatus(int status)
        => status is 301 or 302 or 303 or 307 or 308;

    /// <summary>响应是否带非空的 <c>Location</c>：没有 Location 的 3xx（如 304）不是可跟随的重定向。</summary>
    private static bool HasLocation(AxiosResponse response)
    {
        foreach (var header in response.Headers)
        {
            if (!string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>响应正文反序列化失败（<c>ERR_BAD_RESPONSE</c>），消息带目标类型名。</summary>
    /// <remarks><paramref name="body"/> 为解析失败时的正文原文（调用方已解码时传入）：消息里附上片段，日志能直接看到坏在哪。</remarks>
    internal static AxiosException BadResponse(AxiosConfig? config, HttpRequestMessage? request, Type targetType, Exception inner, string? body = null)
        => BadResponse(config, request, targetType.Name, inner, body);

    /// <summary>响应正文反序列化失败，目标名由调用方给出。</summary>
    /// <remarks><paramref name="body"/> 为空时只在消息里写目标类型，不追加正文片段。</remarks>
    internal static AxiosException BadResponse(AxiosConfig? config, HttpRequestMessage? request, string targetName, Exception inner, string? body = null)
    {
        return new AxiosException(
            $"Response body could not be deserialized to '{targetName}'{BodySuffix(body)}",
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

    /// <summary>请求正文序列化失败（<c>ERR_BAD_REQUEST</c>），原始异常进 <c>Cause</c>。</summary>
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

    /// <summary>
    /// 请求构造阶段失败（默认查询串展平时的取值 / 格式化）。错误码仍是 <c>ERR_BAD_OPTION_VALUE</c>，
    /// 原始异常留在 <see cref="Cause"/>；<see cref="IsRequestConstructionFailure"/> 供外层改判。
    /// </summary>
    internal static AxiosException RequestConstructionFailed(AxiosConfig? config, Exception inner)
    {
        return new AxiosException(
            $"paramsSerializer 抛出了异常，查询串无法生成：{inner.Message}",
            ErrBadOptionValue,
            config,
            request: null,
            response: null,
            innerException: inner)
        {
            IsRequestConstructionFailure = true
        };
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
    /// 回调抛出异常（回调属于请求管线，不以裸类型漏给调用方）；上传阶段取 <c>ERR_BAD_REQUEST</c>，
    /// 下载 / 原始字节阶段取 <c>ERR_BAD_RESPONSE</c>，原始异常在 <see cref="Cause"/> 里。
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

    /// <summary>响应体超过 <c>Limits.MaxResponseBytes</c>；<paramref name="request"/> 允许为 <c>null</c>（短路响应）。</summary>
    internal static AxiosException MaxResponseBytes(AxiosConfig config, HttpRequestMessage? request, long actual, long limit)
    {
        return new AxiosException(
            $"MaxResponseBytes limit of {limit} exceeded (got {actual} bytes)",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>响应体超过单数组上限（<see cref="PlatformCompat.ArrayMaxLength"/>，约 2GB），无法装进 <c>Response.Data</c>。</summary>
    internal static AxiosException ResponseTooLarge(AxiosConfig config, HttpRequestMessage request, long actual)
    {
        return new AxiosException(
            $"Response body of {actual} bytes exceeds the maximum array length ({PlatformCompat.ArrayMaxLength}): "
            + "Response.Data cannot hold it as a single byte[]. "
            + "Set Limits.MaxResponseBytes to fail earlier, or use ResponseType.Stream to read it incrementally.",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>请求体超过 <c>Limits.MaxRequestBytes</c>（<c>ERR_BAD_REQUEST</c>）：发出前拦下，不带副作用。</summary>
    internal static AxiosException MaxRequestBytes(AxiosConfig config, HttpRequestMessage request, long actual, long limit)
    {
        return new AxiosException(
            $"Request body larger than MaxRequestBytes limit ({actual} > {limit} bytes)",
            ErrBadRequest,
            config,
            request,
            null,
            null);
    }
}
