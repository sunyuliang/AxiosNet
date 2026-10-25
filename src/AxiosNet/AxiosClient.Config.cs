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
            // 没有任何按请求项：链整段都是实例默认段，产物标记即为全链长度。
            merged.TransformInheritedRequest = merged.HasTransformRequest ? merged.TransformRequest.Count : 0;
            merged.TransformInheritedResponse = merged.HasTransformResponse ? merged.TransformResponse.Count : 0;
            return merged;
        }

        // 库产出的配置（快捷方法 / 本方法的产物 / 它们的克隆）整体按「实例来源」对待：
        // BaseAddress 与连接级选项一律沿用本实例的值，不能把源实例的搬过来。
        // InstanceOptionsInherited 覆盖经 Clone 的场景；EffectiveMerged 覆盖未克隆、
        // 直接合并的场景（PreparePerCall 的免克隆回放路径）—— 两者对库产出配置等价，
        // 调用方自己 new 的配置两个标记都没有，仍可按请求指定基地址 / 连接级选项。
        var instanceSourced = perCall.InstanceOptionsInherited || perCall.EffectiveMerged;

        merged.Url = perCall.Url ?? merged.Url;
        merged.Params = perCall.Params ?? merged.Params;
        merged.Data = perCall.Data ?? merged.Data;

        // BaseAddress 与连接级选项同属「实例来源」：回放一份生效配置（response.Config）时，
        // 上一实例的基地址不能覆盖目标实例的 —— 否则请求会静默发到旧服务端。
        if (!instanceSourced)
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
        // 走不可变快照而不是直接枚举集合：RemoveHeaders 的公开类型是 ICollection<string>，
        // 走接口枚举会为枚举器装箱（每请求 32 B），而这里是每请求都要跑的路径。
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

        // 只在显式设置时覆盖：new AxiosConfig() 的默认值会把实例默认吞掉。
        // 必须用 if 而不是三元赋值 —— setter 会把 ResponseTypeSet 置 true，回放生效配置时
        // 连「未设置」也会被记成显式意图（自动切换出的 Dynamic/Stream 就此泄漏到下一次请求）。
        if (perCall.ResponseTypeSet)
        {
            merged.ResponseType = perCall.ResponseType;
        }

        // 与 BaseAddress 同理：库产出配置（回放生效配置 / 免克隆合并）的取消令牌描述的是
        // 「来源实例那一次调用」，不能搬到目标实例 —— 否则实例 A 的 response.Config 回放到 B
        // 会把 A 的令牌套到 B 的请求上（B 既控制不了它的取消，生命周期也可能已经结束）。
        // 调用方自己 new 的配置带令牌照常合并（显式按请求令牌）。
        // 必须用 if 而不是三元赋值：setter 无条件置 CancellationTokenSet（与上面 ResponseType
        // 同一陷阱），三元在 else 分支也会调 setter，把「未设置」记成显式意图。
        if (!instanceSourced && perCall.CancellationTokenSet)
        {
            merged.CancellationToken = perCall.CancellationToken;
        }

        // 分组下沉：每组只有一个守卫（组对象为 null = 本请求没碰这一组），
        // 组内「有值才覆盖」交给各自的 MergeFrom，主体不再有十几次同类判断。
        // 首次读取分组会懒创建对象，但只有 perCall 自己已经建出该分组时才会发生。
        if (perCall.CallbacksOrNull is { } incomingCallbacks)
        {
            merged.Callbacks.MergeFrom(incomingCallbacks);
        }

        // 重试策略属按请求项：本次没碰这一组就沿用实例默认（懒创建，不碰就不分配）。
        if (perCall.RetryOrNull is { } incomingRetry)
        {
            merged.Retry.MergeFrom(incomingRetry);
        }

        if (perCall.SerializationOrNull is { } incomingSerialization)
        {
            merged.Serialization.MergeFrom(incomingSerialization);
        }

        if (perCall.LimitsOrNull is { } incomingLimits)
        {
            merged.Limits.MergeFrom(incomingLimits);
        }

        var perCallConnection = perCall.ConnectionOrNull;

        // HttpVersion / VersionPolicy 属按请求项（不是实例级），库产出的配置同样合并。
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

        // 实例级选项必须合并进来，否则按请求写的值到不了 ValidatePerRequestOptions（它判的是生效配置）。
        // 例外：库产出的配置（回放生效配置或免克隆合并）这几项描述的是来源实例，一律沿用本实例默认值。
        // 其中 Auth / WithCredentials / MaxRedirects 的 *Set 标记是语义的一部分：
        // Auth = null 表示「本次请求不带认证」，显式 null 必须能压住实例默认凭据。
        // Auth 随 Connection 整组受同一门禁：回放不携带来源实例的按请求凭据（Audit20261024P2FixTests
        // 锁定的契约 —— 与 BaseAddress / Proxy 同构，目标实例用自己的默认值）。
        if (!instanceSourced && perCallConnection is not null)
        {
            merged.Connection.MergeFrom(perCallConnection);
        }

        // transform 链按「实例默认 + 本次追加」组合，顺序稳定。
        // 去重只覆盖 perCall 自己登记的「继承段」（前 TransformInherited* 项）：回放 response.Config 时，
        // 它的链本身已经是「实例默认 + 上次按请求」（Shortcut / MergeConfig 的产出都是 Defaults.Clone() 的副本），
        // 不去重会让每个实例默认 transform 每回放一次多执行一遍。
        // 本次追加的项（含与默认链同引用的有意重复注册）一律原样保留 —— 去重范围按来源长度算，
        // 不按 merged 当前链长算，否则「调用方有意重复注册同一委托」会被误吞。
        // 产物的「继承段长度」必须在追加**之前**取（追加后前 N 项不变，但链长了会算错），
        // 且必须**无条件**写：它描述的是产物形态，与这次有没有追加无关 ——
        // 只在 perCall.HasTransform* 分支里写，会让「perCall 自己没 transform」的产物把继承段
        // 留成 0（Defaults.Clone 带过来的值），回放时实例默认 transform 再执行一遍。
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

        // 产物标记 = 合并前 merged 的链长（= 实例默认段）：回放这份配置时前 N 项才该去重。
        merged.TransformInheritedRequest = inheritedRequestCount;
        merged.TransformInheritedResponse = inheritedResponseCount;

        return merged;
    }

    /// <summary>
    /// 链的前 <paramref name="count"/> 项里是否已含同一委托实例（按引用）。
    /// <para>链通常 0–3 项，线性扫描比为主路径建 HashSet 便宜；count 是 perCall 登记的「继承段」长度，
    /// 只扫这一段是为了不吞掉本次追加的重复项。</para>
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
            // 取当前版本的只读数组：不可变数组本身就是快照，每请求零分配、零拷贝。
            var headers = config.Headers.Snapshot();
            foreach (var header in headers)
            {
                if (!ContentHeaderNames.Contains(header.Name))
                {
                    // 非 Content-* 头：先给请求头；请求头不认的个别名称再退回内容头。
                    if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value) && content is not null)
                    {
                        content.Headers.TryAddWithoutValidation(header.Name, header.Value);
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
                if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
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
                    content.Headers.Remove(header.Name);
                    content.Headers.TryAddWithoutValidation(header.Name, header.Value);
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
                if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && RequestContentType.HasMultipartBoundary(content.Headers.ContentType))
                {
                    continue;
                }

                // Content-Type 允许用户显式覆盖工厂默认值
                content.Headers.Remove(header.Name);
                content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        // HasHeaders 先判：没有自定义头时不必为一次「有没有 Authorization」的检查建出集合。
        if (config.ConnectionOrNull?.Auth is { } auth
            && !(config.HasHeaders && config.Headers.Has("Authorization"))
            && !request.Headers.Contains("Authorization"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth.ToBasicHeaderValue());
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
