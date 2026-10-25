namespace AxiosNet;

/// <summary>
/// 对应 axios 的 AxiosError：携带 code / config / request / response。
/// 错误码与 axios 对齐（见 <see cref="ErrNetwork"/> 等常量）。
/// </summary>
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

    public bool IsAxiosError => true;

    public string? Code { get; }

    public AxiosConfig? Config { get; }

    /// <summary>
    /// 触发本次错误的 <see cref="System.Net.Http.HttpRequestMessage"/>。
    /// <para>
    /// 请求结束后该对象已随响应释放：Method / RequestUri / Headers 可安全读取，但 <c>Content</c> 会抛
    /// <see cref="ObjectDisposedException"/>；需要落日志的结构化快照请用 <see cref="ToJson"/>。
    /// </para>
    /// </summary>
    public HttpRequestMessage? Request { get; }

    public AxiosResponse? Response { get; }

    /// <summary>HTTP 状态码（有响应时可用，等价于 <c>Response.Status</c>）。</summary>
    public int? Status => Response?.Status;

    /// <summary>底层异常（例如网络错误的 <see cref="System.Net.Http.HttpRequestException"/>、JSON 解析异常）。</summary>
    public Exception? Cause => InnerException;

    private AxiosException(
        string message,
        string? code,
        AxiosConfig? config,
        HttpRequestMessage? request,
        AxiosResponse? response,
        Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        Config = config;
        Request = request;
        Response = response;
    }

    /// <summary>
    /// 对应 axios <c>error.toJSON()</c>：可直接序列化进日志的结构化快照。
    /// </summary>
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

    internal static AxiosException Timeout(AxiosConfig config, HttpRequestMessage request, Exception? inner = null)
    {
        var message = string.IsNullOrWhiteSpace(config.CallbacksOrNull?.TimeoutMessage)
            ? $"timeout of {config.Timeout.TotalMilliseconds:0}ms exceeded"
            : config.CallbacksOrNull!.TimeoutMessage!;

        return new AxiosException(message, ErrConnAborted, config, request, null, inner);
    }

    /// <summary>
    /// 流式正文的空闲超时（<see cref="AxiosConfig.LimitSettings.StreamIdleTimeout"/>）：连续一段时间没读到数据。
    /// 与整体 <see cref="Timeout"/> 分开报，避免把「流挂了」说成「请求超时」。
    /// </summary>
    internal static AxiosException StreamIdleTimeout(AxiosConfig config, HttpRequestMessage? request, TimeSpan idle, Exception? inner = null)
    {
        return new AxiosException(
            $"stream idle timeout of {idle.TotalMilliseconds:0}ms exceeded: no data received",
            ErrConnAborted,
            config,
            request,
            null,
            inner);
    }

    /// <summary>
    /// 流式响应的单帧超过内部上限（没有分帧符的畸形流会把缓冲区撑到不可控）。
    /// </summary>
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

    /// <summary>
    /// JSON 数组流里的单个元素超过上限：元素必须能整块交给序列化器，否则内存占用由服务端决定。
    /// 与 <see cref="StreamFrameTooLarge"/> 同一条线（同一个可配置上限，默认 8 MB），
    /// 只是措辞点明「元素」而不是「帧」——
    /// 数组里的分隔符是 <c>,</c>，说「缺少分帧符」会把人引向错误的方向。
    /// </summary>
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

    /// <summary>
    /// 流式分帧层面的协议错误（顶层不是数组 / JSON 被截断 / 字节不是合法 JSON）。
    /// 与「元素无法反序列化」分开：这里连元素边界都还没确定，调用方要查的是服务端协议，不是 DTO。
    /// </summary>
    internal static AxiosException StreamMalformed(
        AxiosConfig config,
        HttpRequestMessage? request,
        string detail,
        Exception? inner = null)
    {
        return new AxiosException(
            $"Invalid streamed response: {detail}",
            ErrBadResponse,
            config,
            request,
            null,
            inner);
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

    internal static AxiosException InvalidUrl(string url, AxiosConfig? config = null, Exception? inner = null, string? message = null)
    {
        // 超长 URL（>64KB 的查询串）本身就会让 Uri 拒绝解析：整条塞进消息只会让异常信息变成几十万字符。
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

    internal static AxiosException FromResponse(AxiosConfig config, HttpRequestMessage request, AxiosResponse response)
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

    /// <summary>
    /// 响应反序列化出的枚举值不在已定义集合内（序列化器不复核这层：内置 JSON 的 STJ 会把
    /// <c>999</c> 静默转成 <c>(E)999</c>，第三方实现更没有这层义务）。
    /// <para>
    /// 与「正文无法反序列化」同属 <see cref="ErrBadResponse"/>，但消息要点明「值未定义」——
    /// 调用方要改的是服务端数据，不是序列化器配置。
    /// </para>
    /// </summary>
    internal static AxiosException UndefinedEnumValue(
        AxiosConfig? config,
        HttpRequestMessage? request,
        string targetName,
        string value,
        Exception? inner = null)
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

    /// <summary>
    /// 响应侧 <c>transformResponse</c> 抛错：与「正文无法反序列化」同属 <see cref="ErrBadResponse"/>，
    /// 原始异常放在 <see cref="Cause"/> 里。
    /// </summary>
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
    /// 进度回调（<see cref="AxiosConfig.CallbackSettings.OnUploadProgress"/> /
    /// <see cref="AxiosConfig.CallbackSettings.OnDownloadProgress"/>）抛出异常：
    /// 回调是请求管线的一部分，异常不能以裸类型漏给调用方。
    /// <para>
    /// 错误码按方向取：上传阶段正文还没发出去 → <c>ERR_BAD_REQUEST</c>；
    /// 下载阶段正文已开始接收 → <c>ERR_BAD_RESPONSE</c>。原始异常放在 <see cref="Cause"/> 里。
    /// </para>
    /// </summary>
    internal static AxiosException ProgressCallbackFailed(
        AxiosConfig config,
        HttpRequestMessage? request,
        string callbackName,
        Exception inner,
        bool upload)
    {
        return new AxiosException(
            $"{callbackName} 回调抛出异常，请求已中断：{inner.Message}",
            upload ? ErrBadRequest : ErrBadResponse,
            config,
            request,
            null,
            inner);
    }

    internal static AxiosException MaxContentLength(AxiosConfig config, HttpRequestMessage request, long actual, long limit)
    {
        return new AxiosException(
            $"maxContentLength size of {limit} exceeded (got {actual} bytes)",
            ErrBadResponse,
            config,
            request,
            null,
            null);
    }

    /// <summary>
    /// 响应体超过单数组上限（<see cref="Array.MaxLength"/>，约 2GB）：不可能装进 <c>Response.Data</c> 的
    /// <c>byte[]</c>。不拦的话 <c>new byte[total]</c> 只抛一句无上下文的 <see cref="OutOfMemoryException"/>。
    /// </summary>
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
