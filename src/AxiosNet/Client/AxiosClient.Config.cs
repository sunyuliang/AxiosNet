using AxiosNet.Cache;

namespace AxiosNet;

// AxiosClient 的配置合并（Defaults → 单次请求）与请求头分流：Content-* 落到 content，其余落到 request。
public sealed partial class AxiosClient
{
// 配置合并 / Header 处理

    /// <summary>把调用方传入的按请求配置落到实例默认值上；产物不带 <see cref="AxiosConfig.EffectiveMerged"/>。</summary>
    /// <remarks>
    /// 只处理调用方传入的普通配置（<c>EffectiveMerged</c> 为 <c>false</c>）；
    /// 库产出的已合并配置由 <see cref="PreparePerCall"/> 先重新合并。
    /// </remarks>
    private AxiosConfig MergeConfig(AxiosConfig? perCall)
    {
        var merged = Defaults.Clone();

        // *Set 标记只描述「显式写过」：只清标记、留值。
        merged.ObservabilityOrNull?.ClearSetFlags();

        // 标记实例级选项来自本实例默认值。
        merged.InstanceOptionsInherited = true;

        // 解析结果属于上一次发送，合并出来的新配置必须重新解析。
        merged.ResolvedAuthorization = null;
        merged.ResolvedCredentialIdentity = null;

        // 401 重放的正文持有标记只来自本次按请求的门面。
        merged.RetainBodyContent = perCall?.RetainBodyContent ?? false;

        if (perCall is null)
        {
            // 没有按请求项：继承段长度即整条链长。
            merged.TransformInheritedRequest = merged.TransformRequestCount;
            merged.TransformInheritedResponse = merged.TransformResponseCount;
            return merged;
        }

        // 库产出的配置按「实例来源」对待：BaseAddress 与连接级选项沿用本实例的值。
        var instanceSourced = perCall.InstanceOptionsInherited || perCall.EffectiveMerged;

        MergeScalarSettings(merged, perCall, instanceSourced);
        MergeGroupSettings(merged, perCall, instanceSourced);
        MergeTransformChains(merged, perCall);

        return merged;
    }

    /// <summary>合并按请求的顶层项与表单字段：地址、正文、头、时限、正文类型、压缩、取消令牌、校验状态。</summary>
    /// <param name="merged">已带上实例默认值的合并产物。</param>
    /// <param name="perCall">调用方传入的按请求配置。</param>
    /// <param name="instanceSourced">按请求配置是否由库产出（实例来源）。</param>
    private static void MergeScalarSettings(AxiosConfig merged, AxiosConfig perCall, bool instanceSourced)
    {
        // 库产出的配置不搬来源实例的提供方。
        merged.InternalCredentialProvider = instanceSourced ? null : perCall.InternalCredentialProvider;

        merged.Url = perCall.Url ?? merged.Url;

        // Params / Body 按「是否显式赋值」合并，写 null 表示摘掉实例默认值。
        if (perCall.ParamsSet)
        {
            // 调用方的可变字典要隔离一份：请求在途时改它不能改写本次查询串。
            merged.Params = AxiosConfig.IsolateMutableContainer(perCall.Params);
        }

        if (perCall.BodySet)
        {
            merged.Body = perCall.Body;
        }

        // 格式化钩子是实例级默认值：按请求没写就保留。
        // 库产出的配置不搬来源实例的钩子；调用方自己 new 的配置写了就用。
        if (!instanceSourced && perCall.SerializationOrNull is { ValueTextSet: true } perCallSerializationForValueText)
        {
            merged.Serialization.ValueText = perCallSerializationForValueText.ValueText;
        }

        // 短路响应只按请求意图生效，回放生效配置时不命中。
        if (!perCall.EffectiveMerged)
        {
            merged.ShortCircuit = perCall.ShortCircuit;
        }

        // 缓存是实例级共享状态：库产出的配置不搬来源实例的缓存。
        if (!instanceSourced && perCall.Caching is not null)
        {
            merged.Caching = perCall.Caching;
        }

        // BaseAddress 属「实例来源」，不搬来源实例的值；调用方显式写 null 表示摘掉实例默认值。
        if (!instanceSourced && perCall.BaseAddressSet)
        {
            merged.BaseAddress = perCall.BaseAddress;
        }

        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;

        if (perCall.HasHeaders)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删，按请求与实例默认的库内删头名单都在这时生效。
        if (perCall.RemoveHeadersSnapshot is { } perCallRemoveHeaders)
        {
            foreach (var name in perCallRemoveHeaders)
            {
                merged.RemoveHeaders.Add(name);
            }
        }

        ApplyRemoveHeaders(merged);

        if (perCall.ContentTypeSet)
        {
            merged.ContentType = perCall.ContentType;
        }

        if (perCall.TimeoutSet)
        {
            merged.Timeout = perCall.Timeout;
        }

        // setter 会置 ResponseTypeSet。
        if (perCall.ResponseTypeSet)
        {
            merged.ResponseType = perCall.ResponseType;
        }

        // 显式 None 或显式 null 都关掉实例默认的压缩。
        if (perCall.CompressionSet)
        {
            merged.Compression = perCall.Compression;
        }

        // 取消令牌属实例来源，只在显式设置时赋值。
        if (!instanceSourced && perCall.CancellationTokenSet)
        {
            merged.CancellationToken = perCall.CancellationToken;
        }

        // 组对象为 null 表示本请求没碰这一组。
        if (perCall.ObservabilityOrNull is { } incomingObservability)
        {
            merged.Observability.MergeFrom(incomingObservability);
        }

        if (perCall.ValidateStatusSet)
        {
            merged.ValidateStatus = perCall.ValidateStatus;
        }
    }

    /// <summary>合并按请求的分组配置：可观测性、弹性治理、序列化、限制与传输；未碰的组沿用实例默认。</summary>
    /// <param name="merged">已带上实例默认值的合并产物。</param>
    /// <param name="perCall">调用方传入的按请求配置。</param>
    /// <param name="instanceSourced">按请求配置是否由库产出（实例来源）。</param>
    private static void MergeGroupSettings(AxiosConfig merged, AxiosConfig perCall, bool instanceSourced)
    {
        // 治理项按 *Set 覆盖；没碰这一组就沿用实例默认。
        if (perCall.ResilienceOrNull is { } incomingResilience)
        {
            merged.Resilience.MergeFrom(incomingResilience);
        }

        if (perCall.SerializationOrNull is { } incomingSerialization)
        {
            // 注册表属实例级：库产出的配置沿用本实例的登记。
            var ownRegistry = merged.SerializationOrNull?.BodySerializersOrNull;

            merged.Serialization.MergeFrom(incomingSerialization);

            if (instanceSourced)
            {
                merged.Serialization.BodySerializers = ownRegistry!;
            }
        }

        if (perCall.LimitsOrNull is { } incomingLimits)
        {
            merged.Limits.MergeFrom(incomingLimits);
        }

        var perCallTransport = perCall.TransportOrNull;

        // HttpVersion / VersionPolicy / Profile / MaxRate 按请求合并，库产出的配置同样合并。
        if (perCallTransport is not null)
        {
            // 版本两项按 *Set 合并：显式 null 表示回到库默认。
            if (perCallTransport.HttpVersionSet)
            {
                merged.Transport.HttpVersion = perCallTransport.HttpVersion;
            }

            if (perCallTransport.VersionPolicySet)
            {
                merged.Transport.VersionPolicy = perCallTransport.VersionPolicy;
            }

            if (perCallTransport.ProfileSet)
            {
                merged.Transport.Profile = perCallTransport.Profile;
            }

            if (perCallTransport.MaxRateSet)
            {
                merged.Transport.MaxRate = perCallTransport.MaxRate;
            }
        }

        // 库产出的配置沿用本实例默认值；Auth = null 表示本次不带认证。
        if (!instanceSourced && perCallTransport is not null)
        {
            merged.Transport.MergeFrom(perCallTransport);
        }
    }

    /// <summary>把按请求登记的 transform 追加到实例默认链之后，并记下产物里的继承段长度。</summary>
    /// <param name="merged">已带上实例默认值的合并产物。</param>
    /// <param name="perCall">调用方传入的按请求配置。</param>
    private static void MergeTransformChains(AxiosConfig merged, AxiosConfig perCall)
    {
        // 顺序稳定：实例默认段 + 本次追加；继承段长度必须在追加之前取。
        var inheritedRequestCount = merged.TransformRequestCount;
        var inheritedResponseCount = merged.TransformResponseCount;

        if (perCall.HasTransformRequest)
        {
            var inherited = Math.Min(perCall.TransformInheritedRequest, inheritedRequestCount);
            foreach (var transform in perCall.Serialization.TransformRequest)
            {
                if (inherited > 0 && ChainContainsInherited(merged.Serialization.TransformRequest, inherited, transform))
                {
                    continue;
                }

                merged.Serialization.TransformRequest.Add(transform);
            }
        }

        if (perCall.HasTransformResponse)
        {
            var inherited = Math.Min(perCall.TransformInheritedResponse, inheritedResponseCount);
            foreach (var transform in perCall.Serialization.TransformResponse)
            {
                if (inherited > 0 && ChainContainsInherited(merged.Serialization.TransformResponse, inherited, transform))
                {
                    continue;
                }

                merged.Serialization.TransformResponse.Add(transform);
            }
        }

        // 产物标记 = 合并前 merged 的链长（即实例默认段）。
        merged.TransformInheritedRequest = inheritedRequestCount;
        merged.TransformInheritedResponse = inheritedResponseCount;
    }

    /// <summary>解析凭据来源：静态槽位直接映射；槽位为空时调用内部提供方（每请求一次）；结果只存在配置内部，发送前才写到请求头。</summary>
    private static async ValueTask ResolveCredentialAsync(AxiosConfig config)
    {
        // 槽位里有静态凭据时以槽位为准，槽位为空才用内部提供方。
        var slot = config.TransportOrNull?.Auth;
        var provider = slot is null ? config.InternalCredentialProvider : null;

        // 上一次解析出的落点身份作废。
        config.ResolvedCredentialIdentity = null;

        if (slot is null && provider is null)
        {
            // 槽位为空：之前的头值一并作废。
            config.ResolvedAuthorization = null;
            return;
        }

        var context = new AxiosCredentialContext(config);
        var resolved = slot is not null
            ? await slot.ResolveAsync(context, config.CancellationToken).ConfigureAwait(false)
            : await provider!.GetAsync(context, config.CancellationToken).ConfigureAwait(false);

        config.ResolvedAuthorization = resolved?.ToHeaderValue();
    }

    /// <summary>链的前 <paramref name="count"/> 项里是否已含同一委托实例（按引用）。</summary>
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

    // 只能挂在正文上的头（Allow / Expires / Last-Modified 在 .NET 里也登记在 HttpContentHeaders 下）。
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

    /// <summary>把配置里的头写进请求：<c>Content-*</c> 挂到正文上，其余挂到请求上；需要时临时造空正文承载它们。</summary>
    private static void ApplyHeaders(HttpRequestMessage request, AxiosConfig config)
    {
        var content = request.Content;

        // 是否为承载 Content-* 头临时造了空正文（不参与长度校验）。
        var contentIsSynthetic = false;

        // 没有头就整段跳过（Headers 延迟创建）。
        if (config.HasHeaders)
        {
            // 整段用同一版快照。
            var headers = config.Headers.Snapshot();
            foreach (var header in headers)
            {
                if (!ContentHeaderNames.Contains(header.Name))
                {
                    // 非 Content-* 头：先给请求头，不认的再退回内容头。
                    if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value) && content is not null)
                    {
                        content.Headers.TryAddWithoutValidation(header.Name, header.Value);
                    }

                    continue;
                }

                // Content-Type 的空值表示「摘掉正文的媒体类型」：序列化器自报的那份也要去掉。
                if (string.IsNullOrEmpty(header.Value))
                {
                    if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        content?.Headers.Remove(header.Name);
                    }

                    // 其余 Content-* 的空值会清掉 Content-Length / Content-Encoding，直接跳过。
                    continue;
                }

                // Content-Length 必须与正文实际字节数一致。
                if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (content is null || contentIsSynthetic)
                    {
                        // 没有真实正文时跳过。
                        continue;
                    }

                    var actualLength = content.Headers.ContentLength;
                    if (actualLength is long known)
                    {
                        if (!IsLengthHeaderMatching(header.Value, known))
                        {
                            throw AxiosException.InvalidOption($"Content-Length 头（{header.Value.Trim()}）与请求体实际长度（{known} 字节）不一致。" + "内容长度已知时该头由库按实际字节数生成，请删掉这一行。", config);
                        }

                        continue;
                    }

                    // 长度未知（非 seekable 的 Stream 等）时按调用方声明的值写入。
                    content.Headers.Remove(header.Name);
                    content.Headers.TryAddWithoutValidation(header.Name, header.Value);
                    continue;
                }

                // Content-* 只能挂在正文上：需要时补一个空正文。
                if (content is null)
                {
                    content = new ByteArrayContent(Array.Empty<byte>());
                    contentIsSynthetic = true;
                    request.Content = content;
                }

                // multipart 的 Content-Type 带生成的 boundary，不覆盖。
                if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && RequestContentType.HasMultipartBoundary(content.Headers.ContentType))
                {
                    continue;
                }

                // Content-Type 允许显式覆盖工厂默认值。
                content.Headers.Remove(header.Name);
                content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        // 已有显式 Authorization 头时不再写。
        if (config.ResolvedAuthorization is { } credential
            && !request.Headers.Contains(AuthorizationHeader.Name))
        {
            request.Headers.TryAddWithoutValidation(AuthorizationHeader.Name, credential);
        }
    }

    /// <summary>手写的 Content-Length 是否等于实际字节数（非数字一律视为不一致）。</summary>
    private static bool IsLengthHeaderMatching(string headerValue, long actual)
        => long.TryParse(headerValue.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed == actual;

    /// <summary>条件请求头：<c>ETag</c> 写 <c>If-None-Match</c>，没有才用 <c>Last-Modified</c> 写 <c>If-Modified-Since</c>。</summary>
    /// <remarks>
    /// 写在请求消息上而不是配置头里：配置头会随 <see cref="AxiosResponse.Config"/> 回放给下一条请求。
    /// 两个校验器都写不进去时清掉 <see cref="AxiosConfig.RevalidationEntry"/>。
    /// </remarks>
    private static void ApplyConditionalHeaders(HttpRequestMessage request, AxiosConfig config)
    {
        if (config.RevalidationEntry is not { } entry)
        {
            return;
        }

        if (TryAddConditionalHeader(request, "If-None-Match", CacheValidators.Find(entry, CacheValidators.ETag))
            || TryAddConditionalHeader(request, "If-Modified-Since", CacheValidators.Find(entry, CacheValidators.LastModified)))
        {
            return;
        }

        config.RevalidationEntry = null;
    }

    /// <summary>写一条条件请求头（<c>If-None-Match</c> / <c>If-Modified-Since</c>）；空值或含非法字符时跳过并返回 false。</summary>
    private static bool TryAddConditionalHeader(HttpRequestMessage request, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            // 值来自共享后端，同样挡 C0 注入。
            AxiosHeaders.ValidateHeaderValue(name, value);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>把幂等键写进请求模板：模板只建一次，重试 / 换端点 / 对冲复制时随头沿用同一个键。</summary>
    private static void ApplyIdempotencyKey(HttpRequestMessage request, AxiosConfig config)
    {
        if (config.IdempotencyOrNull is not { Enabled: true } settings)
        {
            return;
        }

        var headerName = string.IsNullOrWhiteSpace(settings.HeaderName)
            ? AxiosConfig.IdempotencySettings.DefaultHeaderName
            : settings.HeaderName.Trim();

        bool added;
        try
        {
            // 调用方自己写了同名头：以显式头为准，不覆盖。
            if (request.Headers.Contains(headerName))
            {
                return;
            }

            added = request.Headers.TryAddWithoutValidation(headerName, ResolveIdempotencyKey(settings, config));
        }
        catch (FormatException ex)
        {
            throw AxiosException.InvalidOption($"幂等键头名 \"{headerName}\" 不是合法的 HTTP 头名。", config, ex);
        }

        if (!added)
        {
            throw AxiosException.InvalidOption($"幂等键头名 \"{headerName}\" 不是合法的 HTTP 头名。", config);
        }
    }

    /// <summary>取本次逻辑请求的键：显式键 → 已生成的键 → 工厂 → 默认 Guid；生成后内存化，重发沿用。</summary>
    private static string ResolveIdempotencyKey(AxiosConfig.IdempotencySettings settings, AxiosConfig config)
    {
        if (settings.Key is { } explicitKey)
        {
            var trimmed = explicitKey.Trim();
            if (trimmed.Length == 0)
            {
                throw AxiosException.InvalidOption("显式设置的幂等键是空白：每个逻辑请求必须有一个非空键。", config);
            }

            return trimmed;
        }

        if (settings.GeneratedKey is { } generated)
        {
            return generated;
        }

        string? key;
        try
        {
            key = settings.KeyFactory?.Invoke(config) ?? Guid.NewGuid().ToString("N");
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.InvalidOption("幂等键工厂抛异常。", config, ex);
        }

        key = key?.Trim();

        if (string.IsNullOrEmpty(key))
        {
            throw AxiosException.InvalidOption("幂等键工厂返回了空白：每个逻辑请求必须有一个非空键。", config);
        }

        // 写回配置，重发沿用同一个键。
        settings.GeneratedKey = key;
        return key;
    }
}
