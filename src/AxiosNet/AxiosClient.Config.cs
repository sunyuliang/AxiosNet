namespace AxiosNet;

// AxiosClient 的配置合并与请求头应用：Defaults → 单次请求的合并规则，
// 以及 Content-* 头落到 content、其余头落到 request 的分流逻辑。
public sealed partial class AxiosClient
{
    // ------------------------------------------------------------------
    // 配置合并 / Header 处理
    // ------------------------------------------------------------------

    private AxiosConfig MergeConfig(AxiosConfig? perCall)
    {
        var merged = Defaults.Clone();
        if (perCall is null)
        {
            return merged;
        }

        merged.Url = perCall.Url ?? merged.Url;
        merged.BaseAddress = perCall.BaseAddress ?? merged.BaseAddress;
        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;
        merged.Headers.MergeFrom(perCall.Headers);
        if (perCall.Params is not null)
        {
            merged.Params = perCall.Params;
        }

        if (perCall.Data is not null)
        {
            merged.Data = perCall.Data;
        }

        merged.ContentType = perCall.ContentTypeSet ? perCall.ContentType : merged.ContentType;

        // 用 *Set 标记判断「显式设置」：Timeout=0 / Max*=null 也能按请求覆盖实例默认值。
        if (perCall.TimeoutSet)
        {
            merged.Timeout = perCall.Timeout;
        }

        merged.Auth = perCall.Auth ?? merged.Auth;
        // 只在显式设置时覆盖，否则沿用实例默认（否则 new AxiosConfig() 的默认值会把实例默认吞掉）
        merged.ResponseType = perCall.ResponseTypeSet ? perCall.ResponseType : merged.ResponseType;
        merged.ValidateStatus = perCall.ValidateStatus ?? merged.ValidateStatus;
        merged.CancellationToken = perCall.CancellationTokenSet ? perCall.CancellationToken : merged.CancellationToken;
        merged.HttpVersion = perCall.HttpVersion ?? merged.HttpVersion;
        merged.JsonSerializerOptions = perCall.JsonSerializerOptions ?? merged.JsonSerializerOptions;
        merged.JsonSerializer = perCall.JsonSerializer ?? merged.JsonSerializer;
        merged.RequestEncoding = perCall.RequestEncoding ?? merged.RequestEncoding;
        merged.ResponseEncoding = perCall.ResponseEncoding ?? merged.ResponseEncoding;
        merged.PoolOptions = perCall.PoolOptions ?? merged.PoolOptions;

        // 以下为后加的配置：Proxy/Credentials 已在 ValidatePerRequestOptions 里拦掉「按请求设置」，
        // WithCredentials/MaxRedirects 属实例级，不做合并。
        if (perCall.MaxContentLengthSet)
        {
            merged.MaxContentLength = perCall.MaxContentLength;
        }

        if (perCall.MaxBodyLengthSet)
        {
            merged.MaxBodyLength = perCall.MaxBodyLength;
        }

        if (perCall.MaxRateSet)
        {
            merged.MaxRate = perCall.MaxRate;
        }
        merged.TimeoutMessage = perCall.TimeoutMessage ?? merged.TimeoutMessage;
        merged.OnUploadProgress = perCall.OnUploadProgress ?? merged.OnUploadProgress;
        merged.OnDownloadProgress = perCall.OnDownloadProgress ?? merged.OnDownloadProgress;
        merged.ParamsSerializer = perCall.ParamsSerializer ?? merged.ParamsSerializer;
        merged.FormFieldNameSelector = perCall.FormFieldNameSelector ?? merged.FormFieldNameSelector;

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定
        foreach (var transform in perCall.TransformRequest)
        {
            merged.TransformRequest.Add(transform);
        }

        foreach (var transform in perCall.TransformResponse)
        {
            merged.TransformResponse.Add(transform);
        }

        return merged;
    }

    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Length",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Type",
        "Expires",
        "Last-Modified"
    };

    private static void ApplyHeaders(HttpRequestMessage request, AxiosConfig config)
    {
        var content = request.Content;
        foreach (var header in config.Headers)
        {
            if (string.IsNullOrEmpty(header.Value))
            {
                continue;
            }

            if (content is not null && ContentHeaderNames.Contains(header.Key))
            {
                // Content-Type 允许用户显式覆盖工厂默认值
                content.Headers.Remove(header.Key);
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && content is not null)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (config.Auth is not null
            && !config.Headers.Has("Authorization")
            && !request.Headers.Contains("Authorization"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", config.Auth.ToBasicHeaderValue());
        }
    }
}
