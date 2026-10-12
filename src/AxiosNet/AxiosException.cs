namespace AxiosNet;

/// <summary>
/// 对应 axios 的 AxiosError：携带 code / config / request / response。
/// 错误码与 axios 对齐：ECONNABORTED / ERR_NETWORK / ERR_BAD_REQUEST /
/// ERR_BAD_RESPONSE / ERR_CANCELED / ERR_INVALID_URL。
/// </summary>
public sealed class AxiosException : Exception
{
    public bool IsAxiosError => true;

    public string? Code { get; }

    public AxiosConfig? Config { get; }

    public HttpRequestMessage? Request { get; }

    public AxiosResponse? Response { get; }

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

    internal static AxiosException Timeout(AxiosConfig config, HttpRequestMessage request)
    {
        var ms = config.Timeout.TotalMilliseconds;
        return new AxiosException(
            $"timeout of {ms:0}ms exceeded",
            "ECONNABORTED",
            config,
            request,
            null,
            null);
    }

    internal static AxiosException Canceled(AxiosConfig config, HttpRequestMessage? request)
    {
        return new AxiosException("Request canceled", "ERR_CANCELED", config, request, null, null);
    }

    internal static AxiosException Network(AxiosConfig config, HttpRequestMessage? request, Exception inner)
    {
        return new AxiosException("Network Error", "ERR_NETWORK", config, request, null, inner);
    }

    internal static AxiosException InvalidUrl(string url, Exception? inner = null)
    {
        return new AxiosException($"Invalid URL: {url}", "ERR_INVALID_URL", null, null, null, inner);
    }

    internal static AxiosException FromResponse(AxiosConfig config, HttpRequestMessage request, AxiosResponse response)
    {
        var code = response.Status >= 500 ? "ERR_BAD_RESPONSE" : "ERR_BAD_REQUEST";
        return new AxiosException(
            $"Request failed with status code {response.Status}",
            code,
            config,
            request,
            response,
            null);
    }
}
