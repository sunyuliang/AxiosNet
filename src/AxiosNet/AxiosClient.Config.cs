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

        // 清掉「显式赋值过」标记：它们描述的是实例的值，不是本次按请求的意图。
        merged.ClearInstanceOptionFlags();

        // 实例级选项来自本实例默认值，标成「来源是实例」，回放到别的实例时不再按请求项搬过去。
        merged.InstanceOptionsInherited = true;

        if (perCall is null)
        {
            // 没有按请求项：继承段长度即整条链长。
            merged.TransformInheritedRequest = merged.HasTransformRequest ? merged.TransformRequest.Count : 0;
            merged.TransformInheritedResponse = merged.HasTransformResponse ? merged.TransformResponse.Count : 0;
            return merged;
        }

        // 库产出的配置（快捷方法 / 合并产物 / 它们的克隆）按「实例来源」对待：
        // BaseAddress 与连接级选项沿用本实例的值，不搬来源实例的。
        var instanceSourced = perCall.InstanceOptionsInherited || perCall.EffectiveMerged;

        merged.Url = perCall.Url ?? merged.Url;

        // Params / Data 按「是否显式赋值」合并：按请求写 null 是摘掉实例默认值的唯一表达方式。
        if (perCall.ParamsSet)
        {
            merged.Params = perCall.Params;
        }

        if (perCall.DataSet)
        {
            merged.Data = perCall.Data;
        }

        // 短路响应只按请求意图生效：库产出的生效配置里它是上一次已发生的事，回放时不能再次命中。
        if (!perCall.EffectiveMerged)
        {
            merged.ShortCircuit = perCall.ShortCircuit;
        }

        // BaseAddress 属「实例来源」：回放生效配置时，上一实例的基地址不能覆盖目标实例的。
        if (!instanceSourced)
        {
            merged.BaseAddress = perCall.BaseAddress ?? merged.BaseAddress;
        }

        merged.Method = perCall.MethodSet ? perCall.Method : merged.Method;

        // 空集合时跳过，省掉快照枚举与空列表合并的分配。
        if (perCall.HasHeaders)
        {
            merged.Headers.MergeFrom(perCall.Headers);
        }

        // 合并之后再删：按请求与实例默认的 RemoveHeaders 都在这时生效，两边都没内容时跳过。
        // 走不可变快照枚举，避免走 ICollection<string> 接口枚举时的装箱。
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

        // 只在显式设置时赋值：否则 ContentTypeSet 恒为 true，「自带 Content-Type 就透传」永远失效。
        if (perCall.ContentTypeSet)
        {
            merged.ContentType = perCall.ContentType;
        }

        // 用 *Set 标记判断显式设置：Timeout=0 / Max*=null 也能覆盖实例默认值。
        if (perCall.TimeoutSet)
        {
            merged.Timeout = perCall.Timeout;
        }

        // 总预算同属按请求项；显式 null 是「这一次不要实例默认的总预算」。
        if (perCall.TotalTimeoutSet)
        {
            merged.TotalTimeout = perCall.TotalTimeout;
        }

        // 只在显式设置时覆盖；必须用 if 而不是三元赋值：setter 会把 ResponseTypeSet 置 true。
        if (perCall.ResponseTypeSet)
        {
            merged.ResponseType = perCall.ResponseType;
        }

        // 与 BaseAddress 同理：库产出配置的取消令牌属于来源实例那一次调用，不搬到目标实例。
        // 必须用 if 而不是三元赋值：setter 会无条件置 CancellationTokenSet。
        if (!instanceSourced && perCall.CancellationTokenSet)
        {
            merged.CancellationToken = perCall.CancellationToken;
        }

        // 组对象为 null 表示本请求没碰这一组，组内「有值才覆盖」交给各自的 MergeFrom。
        if (perCall.CallbacksOrNull is { } incomingCallbacks)
        {
            merged.Callbacks.MergeFrom(incomingCallbacks);
        }

        // 重试策略属按请求项：本次没碰这一组就沿用实例默认（懒创建，不碰就不分配）。
        if (perCall.RetryOrNull is { } incomingRetry)
        {
            merged.Retry.MergeFrom(incomingRetry);
        }

        // 熔断同理；显式赋 null 则压住实例默认（健康检查不被熔断挡住）。
        if (perCall.CircuitOrNull is { } incomingCircuit)
        {
            merged.Circuit.MergeFrom(incomingCircuit);
        }

        // 对冲与备用端点同属按请求项：个别慢接口可以单独开对冲 / 换端点，不影响实例默认。
        if (perCall.HedgingOrNull is { } incomingHedging)
        {
            merged.Hedging.MergeFrom(incomingHedging);
        }

        if (perCall.FailoverOrNull is { } incomingFailover)
        {
            merged.Failover.MergeFrom(incomingFailover);
        }

        // 遥测开关同属按请求项：健康检查这类请求可以单独关掉，不影响实例默认。
        if (perCall.TelemetryOrNull is { } incomingTelemetry)
        {
            merged.Telemetry.MergeFrom(incomingTelemetry);
        }

        if (perCall.SerializationOrNull is { } incomingSerialization)
        {
            merged.Serialization.MergeFrom(incomingSerialization);
        }

        if (perCall.LimitsOrNull is { } incomingLimits)
        {
            merged.Limits.MergeFrom(incomingLimits);

            // 并发与排队上限同属实例级：库产出的配置里它们是来源实例的生效值，不搬过来；其余限制项照常合并。
            // 值之外还要还原 *Set 标记：闸门校验按它区分「本次没表态」与「显式要求 null」。
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

        // 实例级选项必须合并进来，按请求写的值才能到达 ValidatePerRequestOptions（它判的是生效配置）。
        // 库产出的配置这几项描述的是来源实例，一律沿用本实例默认值；Auth = null 表示本次不带认证。
        if (!instanceSourced && perCallConnection is not null)
        {
            merged.Connection.MergeFrom(perCallConnection);
        }

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定。
        // 去重只覆盖 perCall 登记的继承段（前 TransformInherited* 项），本次追加的项一律原样保留。
        // 继承段长度必须在追加之前取，且无条件写入。
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

    // 只能挂在正文上的头。Allow / Expires / Last-Modified 在 .NET 里登记在 HttpContentHeaders 下，
    // HttpRequestHeaders 会拒收，因此也留在集合内。
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

        // 这次调用是否为了承载 Content-* 头而临时造了空正文；它不参与长度校验。
        var contentIsSynthetic = false;

        // 没有头就整段跳过：Headers 延迟创建，为「遍历空集合」把它建出来是每请求的白分配。
        if (config.HasHeaders)
        {
            // 取当前版本的数组快照：零分配、零拷贝。
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

                // 空值用在 Content-* 上会清掉 Content-Length / Content-Type，跳过。
                if (string.IsNullOrEmpty(header.Value))
                {
                    continue;
                }

                // Content-Length 必须与正文实际字节数一致，否则连接会一直等一个不会来的正文。
                if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (content is null || contentIsSynthetic)
                    {
                        // 没有真实正文时写它没有意义（传输层会自己补 Content-Length: 0），也不能为它造正文。
                        continue;
                    }

                    var actualLength = content.Headers.ContentLength;
                    if (actualLength is long known)
                    {
                        if (!IsLengthHeaderMatching(header.Value, known))
                        {
                            throw AxiosException.InvalidOption($"Content-Length 头（{header.Value.Trim()}）与请求体实际长度（{known} 字节）不一致。" + "内容长度已知时该头由库按实际字节数生成，请删掉这一行。", config);
                        }

                        // 一致：内容已声明正确长度，不必再写一遍。
                        continue;
                    }

                    // 长度未知（非 seekable 的 Stream 等）：尊重调用方显式声明的值。
                    content.Headers.Remove(header.Name);
                    content.Headers.TryAddWithoutValidation(header.Name, header.Value);
                    continue;
                }

                // Content-* 只能挂在正文上：调用方显式写了内容头时补一个空正文让它落到线上。
                if (content is null)
                {
                    content = new ByteArrayContent(Array.Empty<byte>());
                    contentIsSynthetic = true;
                    request.Content = content;
                }

                // multipart 的 Content-Type 带生成的 boundary，覆盖会让正文无法解析。
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

        // 先判 HasHeaders：没有自定义头时不必为一次 Authorization 检查建出集合。
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
}
