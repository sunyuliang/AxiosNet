using AxiosNet.Internal;

namespace AxiosNet;

// AxiosClient 的配置合并（Defaults → 单次请求）与请求头分流
// （Content-* 落到 content，其余落到 request）。
public sealed partial class AxiosClient
{
    // ------------------------------------------------------------------
    // 配置合并 / Header 处理
    // ------------------------------------------------------------------

    private AxiosConfig MergeConfig(AxiosConfig? perCall)
    {
        var merged = Defaults.Clone();

        // 合并结果的起点是实例默认值：它自己的「显式赋值过」标记不能当成按请求意图带下去，
        // 否则「构造之后改 Defaults.MaxRedirects」会让每个请求都在校验层抛错
        // （文档承诺的是：连接级选项构造后修改不生效，但不会有任何报错）。
        merged.ClearInstanceOptionFlags();

        // 合并结果里的实例级选项来自本实例默认值（按请求改写的已被校验拦掉），
        // 因此同样标成「来源是实例」：回放到另一个实例时不能再被当成按请求项搬过去。
        merged.InstanceOptionsInherited = true;

        if (perCall is null)
        {
            return merged;
        }

        merged.Url = perCall.Url ?? merged.Url;

        // BaseAddress 与连接级选项同属「实例来源」：回放一份生效配置（response.Config）时，
        // 上一实例的基地址不能覆盖目标实例的 —— 否则请求会静默发到旧服务端。
        // 区分依据是 InstanceOptionsInherited（Library 产出的生效配置），
        // 调用方自己 new 的配置（false）仍可按请求指定基地址。
        if (!perCall.InstanceOptionsInherited)
        {
            merged.BaseAddress = perCall.BaseAddress ?? merged.BaseAddress;
        }

        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;

        // 空集合时跳过：快捷方法每请求新建的 config 大概率没有自定义头 / transform，
        // 省掉快照枚举与空列表合并的分配。
        if (perCall.HasHeaders)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删：这样「按请求摘掉实例默认头」才有表达方式。
        // 实例默认（Defaults）自己的 RemoveHeaders 也在此生效 —— 否则同一份默认配置，
        // 快捷方法会摘、RequestAsync 入口不摘，两条入口行为相反；Defaults 上写
        // 「永远摘掉某个头」也才有意义。两边都没内容时跳过（空集合删除是 no-op）。
        if (perCall.HasRemoveHeaders)
        {
            foreach (var name in perCall.RemoveHeaders)
            {
                merged.RemoveHeaders.Add(name);
            }
        }

        if (merged.HasRemoveHeaders && merged.HasHeaders)
        {
            foreach (var name in merged.RemoveHeaders)
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

        // 只在显式设置时赋值：否则 ContentTypeSet 恒为 true，
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

        // 显式赋过值才覆盖：Auth = null 的语义是「本次请求不带认证」，
        // 必须能压住实例默认的凭据（用 ?? 会把实例凭据发到本应匿名的端点）。
        if (perCall.AuthSet)
        {
            merged.Auth = perCall.Auth;
        }

        // 只在显式设置时覆盖：new AxiosConfig() 的默认值会把实例默认吞掉。
        // 必须用 if 而不是三元赋值 —— setter 会把 ResponseTypeSet 置 true，回放生效配置时
        // 连「未设置」也会被记成显式意图（自动切换出的 Dynamic/Stream 就此泄漏到下一次请求）。
        if (perCall.ResponseTypeSet)
        {
            merged.ResponseType = perCall.ResponseType;
        }
        merged.ValidateStatus = perCall.ValidateStatus ?? merged.ValidateStatus;
        merged.CancellationToken = perCall.CancellationTokenSet ? perCall.CancellationToken : merged.CancellationToken;
        merged.HttpVersion = perCall.HttpVersion ?? merged.HttpVersion;
        merged.JsonSerializerOptions = perCall.JsonSerializerOptions ?? merged.JsonSerializerOptions;
        merged.ContentSerializer = perCall.ContentSerializer ?? merged.ContentSerializer;
        merged.RequestEncoding = perCall.RequestEncoding ?? merged.RequestEncoding;
        merged.ResponseEncoding = perCall.ResponseEncoding ?? merged.ResponseEncoding;

        // 后加的配置：Proxy/Credentials 已按「按请求设置」拦掉，WithCredentials/MaxRedirects 属实例级，均不参与合并。
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

        if (perCall.CollectionFormatSet)
        {
            merged.CollectionFormat = perCall.CollectionFormat;
        }

        if (perCall.StreamIdleTimeoutSet)
        {
            merged.StreamIdleTimeout = perCall.StreamIdleTimeout;
        }

        // 实例级选项必须合并进来，否则按请求写的值到不了 ValidatePerRequestOptions（它判的是生效配置）。
        // 例外：回放一份生效配置（InstanceOptionsInherited）时这几项描述的是来源实例，一律沿用本实例默认值。
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

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定。
        // 按委托引用跳过 merged 初始链（= 实例默认链）里已有的项：回放 response.Config 时，
        // 它的链本身已经是「实例默认 + 上次按请求」（Shortcut / MergeConfig 的产出都是 Defaults.Clone() 的副本），
        // 不去重会让每个实例默认 transform 每回放一次多执行一遍。
        // 只对初始链去重：本次追加的项保持原样（调用方有意重复注册同一委托时语义不变）。
        if (perCall.HasTransformRequest)
        {
            var inherited = merged.HasTransformRequest ? merged.TransformRequest.Count : 0;
            foreach (var transform in perCall.TransformRequest)
            {
                if (inherited > 0 && ChainContainsInherited(merged.TransformRequest, inherited, transform))
                {
                    continue;
                }

                merged.TransformRequest.Add(transform);
            }
        }

        if (perCall.HasTransformResponse)
        {
            var inherited = merged.HasTransformResponse ? merged.TransformResponse.Count : 0;
            foreach (var transform in perCall.TransformResponse)
            {
                if (inherited > 0 && ChainContainsInherited(merged.TransformResponse, inherited, transform))
                {
                    continue;
                }

                merged.TransformResponse.Add(transform);
            }
        }

        return merged;
    }

    /// <summary>
    /// 链的前 <paramref name="count"/> 项里是否已含同一委托实例（按引用）。
    /// <para>链通常 0–3 项，线性扫描比为主路径建 HashSet 便宜；只比初始链是为了不吞掉本次追加的重复项。</para>
    /// </summary>
    private static bool ChainContainsInherited(
        IList<Func<object?, AxiosConfig, object?>> chain,
        int count,
        Func<object?, AxiosConfig, object?> transform)
    {
        for (var i = 0; i < count; i++)
        {
            if (ReferenceEquals(chain[i], transform))
            {
                return true;
            }
        }

        return false;
    }

    // 只能挂在正文上的头。Allow / Expires / Last-Modified 看似响应头，但 .NET 把它们登记在
    // HttpContentHeaders 下，HttpRequestHeaders 会拒收（net6.0 / net8.0 一致），
    // 因此仍在集合内 —— 移出只会让这些头被静默丢掉。无正文的 GET 会带上 Content-Length: 0，
    // 这是 BCL 头模型决定的。
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

        // 这次调用是否为了承载 Content-* 头而临时造了一个空正文。
        // 它只用于让头能上线，不能被当成「真实正文」参与长度校验 ——
        // 否则 Content-Type 与 Content-Length 的先后顺序会决定「报错」还是「静默改成 0」。
        var contentIsSynthetic = false;

        // 没有头就整段跳过：Headers 延迟创建，为「遍历空集合」把它建出来是每请求的白分配。
        if (config.HasHeaders)
        {
            // 取缓存的只读数组快照：默认头基本不变，数组只建一次，之后每请求零分配。
            var headers = config.Headers.Snapshot();
            foreach (var header in headers)
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

                // Content-Length 必须与正文实际字节数一致：写错的值会让连接一直等一个不会来的正文，
                // 最终只表现为一句 ERR_NETWORK。
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (content is null || contentIsSynthetic)
                    {
                        // 没有正文时写它没有意义（传输层会自己补 Content-Length: 0），也不能为了它造一个正文。
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

                    // 长度未知（非 seekable 的 Stream 等）：尊重调用方显式声明的值
                    // —— 这是「已知长度、但要关掉 chunked」的逃生口。
                    content.Headers.Remove(header.Key);
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    continue;
                }

                // Content-* 只能挂在正文上：没有正文时 HttpRequestHeaders 会拒收，头就被静默丢掉。
                // 调用方显式写了内容头，补一个空正文让它落到线上（Allow / Expires / Last-Modified 同理）。
                if (content is null)
                {
                    content = new ByteArrayContent(Array.Empty<byte>());
                    contentIsSynthetic = true;
                    request.Content = content;
                }

                // multipart 的 Content-Type 带着生成的 boundary（可能已被进度包装复制），
                // 覆盖会让正文无法解析，因此保留内容自身的头部。
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && RequestContentType.HasMultipartBoundary(content.Headers.ContentType))
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
