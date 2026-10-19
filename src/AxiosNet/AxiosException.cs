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

    public HttpRequestMessage? Request { get; }

    public AxiosResponse? Response { get; }

    /// <summary>HTTP 状态码（有响应时可用，等价于 <c>Response.Status</c>）。</summary>
    public int? Status => Response?.Status;

    internal Exception? Cause => InnerException;

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

    internal static AxiosException Timeout(AxiosConfig config, HttpRequestMessage request)
    {
        var message = string.IsNullOrWhiteSpace(config.TimeoutMessage)
            ? $"timeout of {config.Timeout.TotalMilliseconds:0}ms exceeded"
            : config.TimeoutMessage!;

        return new AxiosException(message, ErrConnAborted, config, request, null, null);
    }

    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request)
    {
        return new AxiosException("Request canceled", ErrCanceled, config, request, null, null);
    }

    internal static AxiosException Network(AxiosConfig config, HttpRequestMessage? request, Exception inner)
    {
        return new AxiosException("Network Error", ErrNetwork, config, request, null, inner);
    }

    internal static AxiosException InvalidUrl(string url, Exception? inner = null)
    {
        return new AxiosException($"Invalid URL: {url}", ErrInvalidUrl, null, null, null, inner);
    }

    internal static AxiosException InvalidOption(string message)
    {
        return new AxiosException(message, ErrBadOptionValue, null, null, null, null);
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

    internal static AxiosException BadResponse(AxiosConfig config, HttpRequestMessage request, Type targetType, Exception inner)
    {
        return new AxiosException(
            $"Response body could not be deserialized to '{targetType.Name}'",
            ErrBadResponse,
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
