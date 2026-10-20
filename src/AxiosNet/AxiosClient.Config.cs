using System.Net.Http.Headers;

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

        // 空集合时跳过：快捷方法（Shortcut）每请求新建的 config 大概率没有自定义头/transform，
        // 省掉快照枚举与空列表合并的分配（热路径上每请求 3 个小对象）。
        if (perCall.Headers.Count > 0)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删：这样「按请求摘掉实例默认头」才有表达方式。
        foreach (var name in perCall.RemoveHeaders)
        {
            merged.Headers.Delete(name);
        }

        if (perCall.Params is not null)
        {
            merged.Params = perCall.Params;
        }

        if (perCall.Data is not null)
        {
            merged.Data = perCall.Data;
        }

        // 只在显式设置时赋值：否则 ContentTypeSet 会被恒置为 true，
        // 让「HttpContent 自带 Content-Type 就原样透传」的判断永远失效。
        if (perCall.ContentTypeSet)
        {
            merged.ContentType = perCall.ContentType;
        }

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
        if (perCall.TransformRequest.Count > 0)
        {
            foreach (var transform in perCall.TransformRequest)
            {
                merged.TransformRequest.Add(transform);
            }
        }

        if (perCall.TransformResponse.Count > 0)
        {
            foreach (var transform in perCall.TransformResponse)
            {
                merged.TransformResponse.Add(transform);
            }
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
            if (content is not null && ContentHeaderNames.Contains(header.Key))
            {
                // 空值用在 Content-* 上会把 Content-Length / Content-Type 直接清掉，保留原有跳过语义。
                if (string.IsNullOrEmpty(header.Value))
                {
                    continue;
                }

                // multipart 的 Content-Type 带着生成的 boundary（可能已被进度包装复制），
                // 覆盖会让正文无法解析，因此保留内容自身的头部。
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && HasMultipartBoundary(content.Headers.ContentType))
                {
                    continue;
                }

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

    /// <summary>内容自身是否已带 multipart boundary（此时不能覆盖 Content-Type）。</summary>
    private static bool HasMultipartBoundary(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType is not { } mediaType
            || !mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var parameter in contentType.Parameters)
        {
            if (parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parameter.Value))
            {
                return true;
            }
        }

        return false;
    }
}
