using AxiosNet.Cache;
using AxiosNet.Internal;

namespace AxiosNet;

// AxiosClient 的配置合并（Defaults → 单次请求）与请求头分流：Content-* 落到 content，其余落到 request。
public sealed partial class AxiosClient
{
    // ------------------------------------------------------------------
    // 配置合并 / Header 处理
    // ------------------------------------------------------------------

    private AxiosConfig MergeConfig(AxiosConfig? perCall)
    {
        var merged = Defaults.Clone();

        // 克隆带过来的标记属于上一份配置，必须清掉，否则本次合并会把继承值与显式设置混为一谈。
        // 只清 WithCredentials / MaxRedirects 两项：Proxy / Credentials 没有 *Set 标记，靠引用为空判断。
        merged.ClearInstanceOptionFlags();

        // 标记实例级选项来自本实例默认值。
        merged.InstanceOptionsInherited = true;

        if (perCall is null)
        {
            // 没有按请求项：继承段长度即整条链长。
            merged.TransformInheritedRequest = merged.HasTransformRequest ? merged.TransformRequest.Count : 0;
            merged.TransformInheritedResponse = merged.HasTransformResponse ? merged.TransformResponse.Count : 0;
            return merged;
        }

        // 库产出的配置（快捷方法 / 合并产物 / 克隆）按「实例来源」对待：
        // BaseAddress 与连接级选项沿用本实例的值。
        var instanceSourced = perCall.InstanceOptionsInherited || perCall.EffectiveMerged;

        merged.Url = perCall.Url ?? merged.Url;

        // Params / Data 按「是否显式赋值」合并，写 null 表示摘掉实例默认值。
        if (perCall.ParamsSet)
        {
            merged.Params = perCall.Params;
        }

        if (perCall.DataSet)
        {
            merged.Data = perCall.Data;
        }

        // 短路响应只按请求意图生效，回放生效配置时不命中。
        if (!perCall.EffectiveMerged)
        {
            merged.ShortCircuit = perCall.ShortCircuit;
        }

        // 缓存是实例级的共享状态：按请求项给了就用它，否则沿用实例默认。
        if (perCall.Cache is not null)
        {
            merged.Cache = perCall.Cache;
        }

        // BaseAddress 属「实例来源」，不搬来源实例的值。
        if (!instanceSourced)
        {
            merged.BaseAddress = perCall.BaseAddress ?? merged.BaseAddress;
        }

        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;

        if (perCall.HasHeaders)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删，按请求与实例默认的 RemoveHeaders 都在这时生效。
        if (perCall.RemoveHeadersSnapshot is { } perCallRemoveHeaders)
        {
            foreach (var name in perCallRemoveHeaders)
            {
                merged.RemoveHeaders.Add(name);
            }
        }

        if (merged.HasHeaders && merged.RemoveHeadersSnapshot is { } removeHeaders)
        {
            foreach (var name in removeHeaders)
            {
                merged.Headers.Delete(name);
            }
        }

        // 只在显式设置时赋值。
        if (perCall.ContentTypeSet)
        {
            merged.ContentType = perCall.ContentType;
        }

        // 按 *Set 标记判断显式设置。
        if (perCall.TimeoutSet)
        {
            merged.Timeout = perCall.Timeout;
        }

        // 总预算属按请求项；显式 null 表示本次不要实例默认的总预算。
        if (perCall.TotalTimeoutSet)
        {
            merged.TotalTimeout = perCall.TotalTimeout;
        }

        // 只在显式设置时覆盖（setter 会置 ResponseTypeSet）。
        if (perCall.ResponseTypeSet)
        {
            merged.ResponseType = perCall.ResponseType;
        }

        // 请求体压缩属按请求项；显式 None 用于关掉实例默认的压缩。
        merged.RequestCompression = perCall.RequestCompression ?? merged.RequestCompression;

        // 取消令牌同属「实例来源」，不搬到目标实例；setter 会置 CancellationTokenSet，故只在显式设置时赋值。
        if (!instanceSourced && perCall.CancellationTokenSet)
        {
            merged.CancellationToken = perCall.CancellationToken;
        }

        // 组对象为 null 表示本请求没碰这一组。
        if (perCall.CallbacksOrNull is { } incomingCallbacks)
        {
            merged.Callbacks.MergeFrom(incomingCallbacks);
        }

        // 重试策略属按请求项，没碰这一组就沿用实例默认（懒创建）。
        if (perCall.RetryOrNull is { } incomingRetry)
        {
            merged.Retry.MergeFrom(incomingRetry);
        }

        // 熔断同理；显式赋 null 则压住实例默认。
        if (perCall.CircuitOrNull is { } incomingCircuit)
        {
            merged.Circuit.MergeFrom(incomingCircuit);
        }

        // 对冲与备用端点同属按请求项。
        if (perCall.HedgingOrNull is { } incomingHedging)
        {
            merged.Hedging.MergeFrom(incomingHedging);
        }

        if (perCall.FailoverOrNull is { } incomingFailover)
        {
            merged.Failover.MergeFrom(incomingFailover);
        }

        // 遥测开关同属按请求项。
        if (perCall.TelemetryOrNull is { } incomingTelemetry)
        {
            merged.Telemetry.MergeFrom(incomingTelemetry);
        }

        // 幂等键同属按请求项；已生成的键不跨合并搬运（那是某次发送的状态）。
        if (perCall.IdempotencyOrNull is { } incomingIdempotency)
        {
            merged.Idempotency.MergeFrom(incomingIdempotency);
        }

        if (perCall.SerializationOrNull is { } incomingSerialization)
        {
            // 注册表属实例级：库产出的配置（回放 response.Config 等）沿用本实例的登记，不搬来源实例的。
            var ownRegistry = merged.SerializationOrNull?.ContentSerializers;

            merged.Serialization.MergeFrom(incomingSerialization);

            if (instanceSourced)
            {
                merged.Serialization.ContentSerializers = ownRegistry;
            }
        }

        if (perCall.LimitsOrNull is { } incomingLimits)
        {
            merged.Limits.MergeFrom(incomingLimits);

            // 并发与排队上限同属实例级，库产出的配置不搬过来；其余限制项照常合并。
            // *Set 标记需一并还原：闸门校验按它区分「本次没表态」与「显式要求 null」。
            if (instanceSourced)
            {
                var ownLimits = Defaults.LimitsOrNull;
                merged.Limits.MaxConcurrentRequests = ownLimits?.MaxConcurrentRequests;
                merged.Limits.MaxConcurrentRequestsSet = ownLimits?.MaxConcurrentRequestsSet ?? false;
                merged.Limits.MaxQueueLength = ownLimits?.MaxQueueLength;
                merged.Limits.MaxQueueLengthSet = ownLimits?.MaxQueueLengthSet ?? false;
            }
        }

        var perCallConnection = perCall.ConnectionOrNull;

        // HttpVersion / VersionPolicy 属按请求项，库产出的配置同样合并。
        if (perCallConnection is not null)
        {
            if (perCallConnection.HttpVersion is { } httpVersion)
            {
                merged.Connection.HttpVersion = httpVersion;
            }

            if (perCallConnection.VersionPolicy is { } versionPolicy)
            {
                merged.Connection.VersionPolicy = versionPolicy;
            }
        }

        // 实例级选项需合并：ValidatePerRequestOptions 校验的是生效配置；库产出的配置沿用本实例默认值。
        // Auth = null 表示本次不带认证。
        if (!instanceSourced && perCallConnection is not null)
        {
            merged.Connection.MergeFrom(perCallConnection);
        }

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定；去重只覆盖 perCall 登记的继承段。
        // 继承段长度必须在追加之前取。
        var inheritedRequestCount = merged.HasTransformRequest ? merged.TransformRequest.Count : 0;
        var inheritedResponseCount = merged.HasTransformResponse ? merged.TransformResponse.Count : 0;

        if (perCall.HasTransformRequest)
        {
            var inherited = Math.Min(perCall.TransformInheritedRequest, inheritedRequestCount);
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
            var inherited = Math.Min(perCall.TransformInheritedResponse, inheritedResponseCount);
            foreach (var transform in perCall.TransformResponse)
            {
                if (inherited > 0 && ChainContainsInherited(merged.TransformResponse, inherited, transform))
                {
                    continue;
                }

                merged.TransformResponse.Add(transform);
            }
        }

        // 产物标记 = 合并前 merged 的链长（即实例默认段）。
        merged.TransformInheritedRequest = inheritedRequestCount;
        merged.TransformInheritedResponse = inheritedResponseCount;

        return merged;
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

    private static void ApplyHeaders(HttpRequestMessage request, AxiosConfig config)
    {
        var content = request.Content;

        // 是否为承载 Content-* 头临时造了空正文（不参与长度校验）。
        var contentIsSynthetic = false;

        // 没有头就整段跳过（Headers 延迟创建）。
        if (config.HasHeaders)
        {
            // 整段用同一版快照：并发 Set 换的是整个数组，逐次读字段会拿到撕裂的组合。
            var headers = config.Headers.Snapshot();
            foreach (var header in headers)
            {
                if (!ContentHeaderNames.Contains(header.Name))
                {
                    // 非 Content-* 头：先给请求头，请求头不认的再退回内容头。
                    if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value) && content is not null)
                    {
                        content.Headers.TryAddWithoutValidation(header.Name, header.Value);
                    }

                    continue;
                }

                // 空值会清掉 Content-Length / Content-Type，跳过。
                if (string.IsNullOrEmpty(header.Value))
                {
                    continue;
                }

                // Content-Length 必须与正文实际字节数一致。
                if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (content is null || contentIsSynthetic)
                    {
                        // 没有真实正文时跳过（不为它造正文）。
                        continue;
                    }

                    var actualLength = content.Headers.ContentLength;
                    if (actualLength is long known)
                    {
                        if (!IsLengthHeaderMatching(header.Value, known))
                        {
                            throw AxiosException.InvalidOption($"Content-Length 头（{header.Value.Trim()}）与请求体实际长度（{known} 字节）不一致。" + "内容长度已知时该头由库按实际字节数生成，请删掉这一行。", config);
                        }

                        // 一致时无需重写。
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

        // 先判 HasHeaders，避免无谓创建头集合。
        if (config.ConnectionOrNull?.Auth is { } auth
            && !(config.HasHeaders && config.Headers.Has("Authorization"))
            && !request.Headers.Contains("Authorization"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth.ToBasicHeaderValue());
        }
    }

    /// <summary>手写的 Content-Length 是否等于实际字节数（非数字一律视为不一致）。</summary>
    private static bool IsLengthHeaderMatching(string headerValue, long actual)
        => long.TryParse(headerValue.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed == actual;

    /// <summary>
    /// 条件请求头：旧条目的 <c>ETag</c> 写成 <c>If-None-Match</c>（更强的校验器），没有才用 <c>Last-Modified</c> 写 <c>If-Modified-Since</c>。
    /// </summary>
    /// <remarks>
    /// 写在请求消息上而不是配置头里：配置头会随 <see cref="AxiosResponse.Config"/> 回放给下一条请求，条件头不该跟着走。
    /// 两个校验器都写不进去时清掉 <see cref="AxiosConfig.RevalidationEntry"/>，终端就不会把召回的 304 当成本库的条件请求结果。
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

    private static bool TryAddConditionalHeader(HttpRequestMessage request, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            // 存储可能来自共享后端：值同样挡 C0 注入。
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

        settings.GeneratedKey = key;
        return key;
    }
}
