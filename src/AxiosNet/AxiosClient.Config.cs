using AxiosNet.Internal;

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

        // 合并结果里的实例级选项来自本实例默认值（按请求改写的那几项会在 ValidatePerRequestOptions
        // 里被拦掉），所以它同样带「来源是实例」标记：这份配置作为 response.Config 被回放到另一个
        // 实例时，不能再被当成按请求项搬过去。
        merged.InstanceOptionsInherited = true;

        if (perCall is null)
        {
            return merged;
        }

        merged.Url = perCall.Url ?? merged.Url;
        merged.BaseAddress = perCall.BaseAddress ?? merged.BaseAddress;
        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;

        // 空集合时跳过：快捷方法（Shortcut）每请求新建的 config 大概率没有自定义头/transform，
        // 省掉快照枚举与空列表合并的分配（热路径上每请求 3 个小对象）。
        if (perCall.HasHeaders)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删：这样「按请求摘掉实例默认头」才有表达方式。
        // 两边都没内容时整段跳过——Delete 对空集合是 no-op，却会把空集合创建出来。
        if (perCall.HasRemoveHeaders && merged.HasHeaders)
        {
            foreach (var name in perCall.RemoveHeaders)
            {
                merged.Headers.Delete(name);
            }
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
        merged.ContentSerializer = perCall.ContentSerializer ?? merged.ContentSerializer;
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

        // 实例级选项：必须合并进来，否则按请求写的值根本到不了 ValidatePerRequestOptions
        // （它判定的是「生效配置」）—— 表现就是「设了没生效、也不报错」，与本类文档承诺相反。
        // 判定在验证阶段做（值不同才报错），这里只负责把按请求项搬过来。
        //
        // 例外：回放一份生效配置（InstanceOptionsInherited）时这几项描述的是<b>来源实例</b>，
        // 不是本次调用方写的按请求项。搬过来既会用错连接池，又会被校验判成非法按请求项 ——
        // 一律沿用本实例的默认值。
        if (!perCall.InstanceOptionsInherited)
        {
            if (perCall.WithCredentialsSet)
            {
                merged.WithCredentials = perCall.WithCredentials;
            }

            if (perCall.MaxRedirectsSet)
            {
                merged.MaxRedirects = perCall.MaxRedirects;
            }

            merged.Proxy = perCall.Proxy ?? merged.Proxy;
            merged.Credentials = perCall.Credentials ?? merged.Credentials;
            merged.PoolOptions = perCall.PoolOptions ?? merged.PoolOptions;
        }

        merged.TimeoutMessage = perCall.TimeoutMessage ?? merged.TimeoutMessage;
        merged.OnUploadProgress = perCall.OnUploadProgress ?? merged.OnUploadProgress;
        merged.OnDownloadProgress = perCall.OnDownloadProgress ?? merged.OnDownloadProgress;
        merged.ParamsSerializer = perCall.ParamsSerializer ?? merged.ParamsSerializer;
        merged.FormFieldNameSelector = perCall.FormFieldNameSelector ?? merged.FormFieldNameSelector;

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定
        if (perCall.HasTransformRequest)
        {
            foreach (var transform in perCall.TransformRequest)
            {
                merged.TransformRequest.Add(transform);
            }
        }

        if (perCall.HasTransformResponse)
        {
            foreach (var transform in perCall.TransformResponse)
            {
                merged.TransformResponse.Add(transform);
            }
        }

        return merged;
    }

    // 只能挂在正文上的头。
    // 前三项（Allow / Expires / Last-Modified）看起来是响应侧头，但 .NET 的强类型头集合
    // 把它们登记在 HttpContentHeaders 下：HttpRequestHeaders.TryAddWithoutValidation 对它们
    // **返回 false**（实测 net6.0 / net8.0 一致，Expires / Last-Modified / Allow 三项皆然）。
    // 因此这里不能把它们移出集合 —— 移出后回落分支只会静默丢掉这个头，
    // 比「补一个空正文把它发出去」更糟。无正文的 GET 上设这几个头会带上 Content-Length: 0，
    // 这是 BCL 的头模型决定的，不是本库的取舍。
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

        // 没有头就整段跳过：config.Headers 是延迟创建的，为「遍历一个空集合」把它建出来
        // 是每请求 ~200 B 的白分配（绝大多数快捷方法调用都不带自定义头）。
        if (config.HasHeaders)
        {
            foreach (var header in config.Headers)
            {
                if (!ContentHeaderNames.Contains(header.Key))
                {
                    // 非 Content-* 头：先给请求头；请求头不认的个别名称再退回内容头。
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && content is not null)
                    {
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    continue;
                }

                // 空值用在 Content-* 上会把 Content-Length / Content-Type 直接清掉，保留原有跳过语义。
                if (string.IsNullOrEmpty(header.Value))
                {
                    continue;
                }

                // Content-Length 必须与正文实际字节数一致：写错的值会让连接一直等一个永远不会来的正文，
                // 最终只表现为一句 ERR_NETWORK（真实原因埋在几十层堆栈下面）。
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (content is null)
                    {
                        // 没有正文时写它没有任何意义（传输层会自己补 Content-Length: 0），
                        // 也不能为了它就造一个正文。
                        continue;
                    }

                    var actualLength = content.Headers.ContentLength;
                    if (actualLength is long known)
                    {
                        if (!IsLengthHeaderMatching(header.Value, known))
                        {
                            throw AxiosException.InvalidOption(
                                $"Content-Length 头（{header.Value.Trim()}）与请求体实际长度（{known} 字节）不一致。"
                                + "内容长度已知时该头由库按实际字节数生成，请删掉这一行。",
                                config);
                        }

                        // 一致：内容自身已经声明了正确长度，不必再写一遍
                        continue;
                    }

                    // 长度未知（非 seekable 的 Stream 等）：尊重调用方显式声明的值 ——
                    // 这是「已知长度、但要关掉 chunked」的逃生口。
                    content.Headers.Remove(header.Key);
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    continue;
                }

                // Content-* 头只能挂在正文上：没有正文时 HttpRequestHeaders 会直接拒收
                // （TryAddWithoutValidation 返回 false），这个头就被静默丢掉了。
                // 调用方显式写了内容头，说明这次请求就该带上它 —— 补一个空正文让它落到线上。
                // 注意 Allow / Expires / Last-Modified 在 BCL 里同样属于内容头，也走这条路径。
                if (content is null)
                {
                    content = new ByteArrayContent(Array.Empty<byte>());
                    request.Content = content;
                }

                // multipart 的 Content-Type 带着生成的 boundary（可能已被进度包装复制），
                // 覆盖会让正文无法解析，因此保留内容自身的头部。
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && RequestBodyFactory.HasMultipartBoundary(content.Headers.ContentType))
                {
                    continue;
                }

                // Content-Type 允许用户显式覆盖工厂默认值
                content.Headers.Remove(header.Key);
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // HasHeaders 先判：没有自定义头时不必为一次「有没有 Authorization」的检查建出集合。
        if (config.Auth is not null
            && !(config.HasHeaders && config.Headers.Has("Authorization"))
            && !request.Headers.Contains("Authorization"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", config.Auth.ToBasicHeaderValue());
        }
    }

    /// <summary>手写的 Content-Length 是否等于实际字节数（非数字一律视为不一致）。</summary>
    private static bool IsLengthHeaderMatching(string headerValue, long actual)
        => long.TryParse(
                headerValue.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            && parsed == actual;
}
