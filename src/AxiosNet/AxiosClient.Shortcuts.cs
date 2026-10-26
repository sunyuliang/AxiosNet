using AxiosNet.Internal;

namespace AxiosNet;

// AxiosClient 的快捷方法层：与 axios.get/post/put/patch/delete/head/options 及
// postForm/putForm/patchForm 一一对应，只负责组装 AxiosConfig。
public sealed partial class AxiosClient
{
    // ------------------------------------------------------------------
    // 快捷方法（与 axios.get/post/put/patch/delete/head/options 对齐）
    // ------------------------------------------------------------------

    public Task<AxiosResponse> GetAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Get, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> GetAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Get, configure, cancellationToken));

    /// <summary>GET 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> GetAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Get, configure, cancellationToken, data), null);

    /// <summary>GET 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> GetAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Get, configure, cancellationToken, data));

    public Task<AxiosResponse> GetAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Get), null);

    public Task<AxiosResponse<T>> GetAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Get));

    public Task<AxiosResponse> DeleteAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Delete, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Delete, configure, cancellationToken));

    /// <summary>DELETE 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> DeleteAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Delete, configure, cancellationToken, data), null);

    /// <summary>DELETE 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Delete, configure, cancellationToken, data));

    public Task<AxiosResponse> DeleteAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Delete), null);

    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Delete));

    public Task<AxiosResponse> HeadAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Head, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> HeadAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Head, configure, cancellationToken));

    /// <summary>HEAD 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> HeadAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Head, configure, cancellationToken, data), null);

    /// <summary>HEAD 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> HeadAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Head, configure, cancellationToken, data));

    public Task<AxiosResponse> OptionsAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Options, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Options, configure, cancellationToken));

    /// <summary>OPTIONS 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> OptionsAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Options, configure, cancellationToken, data), null);

    /// <summary>OPTIONS 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Options, configure, cancellationToken, data));

    public Task<AxiosResponse> PostAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Post, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PostAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Post, configure, cancellationToken, data));

    public Task<AxiosResponse> PostAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Post), null);

    public Task<AxiosResponse<T>> PostAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Post));

    public Task<AxiosResponse> PutAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Put, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PutAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Put, configure, cancellationToken, data));

    public Task<AxiosResponse> PatchAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Patch, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PatchAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Patch, configure, cancellationToken, data));

    // ------------------------------------------------------------------
    // 表单快捷方法（对应 axios postForm / putForm / patchForm）
    // ------------------------------------------------------------------

    /// <summary>对应 axios postForm：默认按 multipart/form-data 发送（字段 + FormFile 文件部件）。</summary>
    public Task<AxiosResponse> PostFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(FormShortcut(url, HttpMethod.Post, configure, cancellationToken, data), null);

    /// <summary>对应 axios postForm。</summary>
    public Task<AxiosResponse<T>> PostFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Post, configure, cancellationToken, data));

    /// <summary>对应 axios putForm。</summary>
    public Task<AxiosResponse> PutFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(FormShortcut(url, HttpMethod.Put, configure, cancellationToken, data), null);

    /// <summary>对应 axios putForm。</summary>
    public Task<AxiosResponse<T>> PutFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Put, configure, cancellationToken, data));

    /// <summary>对应 axios patchForm。</summary>
    public Task<AxiosResponse> PatchFormAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(FormShortcut(url, HttpMethod.Patch, configure, cancellationToken, data), null);

    /// <summary>对应 axios patchForm。</summary>
    public Task<AxiosResponse<T>> PatchFormAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Patch, configure, cancellationToken, data));

    // ------------------------------------------------------------------
    // CancellationToken 显式重载
    // CancellationToken 是 struct：没有这些重载时 GetAsync(url, ct) 会被装箱成 object，
    // 落到 data 上把令牌当请求体序列化。
    // ------------------------------------------------------------------

    public Task<AxiosResponse> GetAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Get, null, cancellationToken), null);

    public Task<AxiosResponse<T>> GetAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Get, null, cancellationToken));

    public Task<AxiosResponse> DeleteAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Delete, null, cancellationToken), null);

    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Delete, null, cancellationToken));

    public Task<AxiosResponse> HeadAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Head, null, cancellationToken), null);

    public Task<AxiosResponse<T>> HeadAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Head, null, cancellationToken));

    public Task<AxiosResponse> OptionsAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Options, null, cancellationToken), null);

    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Options, null, cancellationToken));

    public Task<AxiosResponse> PostAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Post, null, cancellationToken), null);

    public Task<AxiosResponse<T>> PostAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Post, null, cancellationToken));

    public Task<AxiosResponse> PutAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Put, null, cancellationToken), null);

    public Task<AxiosResponse<T>> PutAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Put, null, cancellationToken));

    public Task<AxiosResponse> PatchAsync(string url, CancellationToken cancellationToken)
        => SendAsync(Shortcut(url, HttpMethod.Patch, null, cancellationToken), null);

    public Task<AxiosResponse<T>> PatchAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Patch, null, cancellationToken));

    public Task<AxiosResponse> PostFormAsync(string url, CancellationToken cancellationToken)
        => SendAsync(FormShortcut(url, HttpMethod.Post, null, cancellationToken, null), null);

    public Task<AxiosResponse<T>> PostFormAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Post, null, cancellationToken, null));

    public Task<AxiosResponse> PutFormAsync(string url, CancellationToken cancellationToken)
        => SendAsync(FormShortcut(url, HttpMethod.Put, null, cancellationToken, null), null);

    public Task<AxiosResponse<T>> PutFormAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Put, null, cancellationToken, null));

    public Task<AxiosResponse> PatchFormAsync(string url, CancellationToken cancellationToken)
        => SendAsync(FormShortcut(url, HttpMethod.Patch, null, cancellationToken, null), null);

    public Task<AxiosResponse<T>> PatchFormAsync<T>(string url, CancellationToken cancellationToken)
        => SendTypedAsync<T>(FormShortcut(url, HttpMethod.Patch, null, cancellationToken, null));

    /// <summary>
    /// 表单快捷方法的公共入口：把「默认 multipart」交给 <see cref="Shortcut"/>，
    /// 由它决定这个默认值与实例默认值谁生效（见 <see cref="IsFormContentType"/>）。
    /// </summary>
    private AxiosConfig FormShortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data)
        => Shortcut(url, method, configure, cancellationToken, data, AxiosContentType.MultipartFormData);

    private async Task<AxiosResponse<T>> SendTypedAsync<T>(AxiosConfig config)
    {
        // 目标类型的响应对象在这里造好交给管线就地填充：省掉「管线造非泛型响应 → AsTyped 复制」
        // 的每请求两个响应对象。
        var typed = new AxiosResponse<T>();
        var response = await SendAsync(config, typeof(T), typed).ConfigureAwait(false);

        // 响应拦截器把对象换掉了（它写不出泛型参数，只能给回非泛型响应）：走复制这条路径。
        if (!ReferenceEquals(response, typed))
        {
            return AsTyped<T>(response);
        }

        // Data 必须是 T：responseType 与泛型参数不匹配时给 ERR_BAD_RESPONSE，
        // 而不是把检查推迟到 Data getter（getter 抛异常会让日志、调试器、序列化一起炸）。
        try
        {
            DataCast.EnsureCompatible<T>(typed.RawData, typed.Config, typed.Request);
        }
        catch
        {
            // 这个响应已经交不到调用方手里了：流式正文必须在这里释放，否则租约永不归还。
            // 唯一例外是短路响应（Request == null）：Data 的所有权在调用方，类型不匹配也不能替它释放。
            if (typed.Request is not null)
            {
                (typed.RawData as IDisposable)?.Dispose();
            }

            typed.RawData = null;
            throw;
        }

        return typed;
    }

    /// <summary>把拦截器换回来的非泛型响应复制成 <typeparamref name="T"/>。</summary>
    private static AxiosResponse<T> AsTyped<T>(AxiosResponse response)
    {
        try
        {
            return response.AsTyped<T>();
        }
        catch
        {
            // 泛型参数与 responseType 不匹配（如 stream + GetAsync<byte[]>）：响应已交不出去，
            // 流式正文必须在这里释放，否则租约永不归还。短路响应除外（Data 归调用方）。
            if (response.Request is not null)
            {
                (response.Data as IDisposable)?.Dispose();
            }

            response.Data = null;
            throw;
        }
    }

    /// <summary>
    /// 组装一次快捷调用的<b>生效配置</b>：直接在实例默认值的副本上填写本次请求项，
    /// 并用 <see cref="AxiosConfig.EffectiveMerged"/> 告诉管线不必再合并。
    /// </summary>
    private AxiosConfig Shortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data = null,
        string? defaultContentType = null)
    {
        var config = NewEffectiveConfig();

        // url 是必填参数：null / 空白不能静默解释成「请求 baseAddress 本身」——
        // 那会让一次「变量忘赋值」变成向根路径发出的意外请求（POST / 可能带副作用）。
        // 要请求基地址本身请显式写 "/"。
        if (string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(
                url ?? string.Empty,
                config,
                message: "url 不能为 null 或空白：快捷方法的第一个参数是必填的请求地址"
                    + "（要请求 baseAddress 本身请显式传 \"/\"）。");
        }

        config.Url = url;
        config.Method = method;

        // 表单快捷方法：编码由方法名承诺，优先级 configure > 方法默认 > 实例默认值。
        // 这里不能看 ContentTypeSet —— 它是 Defaults.Clone() 带进来的，为 true 只说明「实例上设过默认值」，
        // 不说明本次调用方有意见：实例为了 JSON 接口设过 application/json 之后，PostFormAsync
        // 会把表单字段按 JSON 发出去，与方法文档相反且毫无提示。
        // 例外：实例默认值本身就是表单编码（urlencoded，或带自定义 boundary 的 multipart），
        // 那是「这个客户端专发表单」的选择，保留它（含 boundary 参数）。
        if (defaultContentType is not null && !IsFormContentType(config.ContentType))
        {
            config.ContentType = defaultContentType;
        }

        // 仅在调用方传入可取消的令牌时才写入，避免用 CancellationToken.None 覆盖掉实例默认令牌
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        // (object)token 之类的写法仍可能落到 data 上：直接报错，别把令牌序列化成请求体。
        if (data is CancellationToken)
        {
            throw AxiosException.InvalidOption(
                "CancellationToken 不能作为请求体传入，请使用 cancellationToken 参数。", config);
        }

        if (data is not null)
        {
            config.Data = data;
        }

        configure?.Invoke(config);
        ApplyRemoveHeaders(config);

        return config;
    }

    /// <summary>
    /// 「本次请求的生效配置」的起点：实例默认值的副本。
    /// <para>
    /// 所有 <c>Action&lt;AxiosConfig&gt;</c> 入口（快捷方法与 <c>RequestAsync(configure)</c>）都从这里开始，
    /// 回调因此能看到并修改实例默认值——<c>config.Headers.Delete("Authorization")</c> 这类
    /// 「删掉就没有了」的操作才能真的摘掉默认头。另一种起点是空配置 + 事后合并：
    /// 删除落在空集合上会被静默吞掉（凭据残留），两条入口的表现还会正好相反。
    /// </para>
    /// <para>
    /// 副本上的实例级选项标记必须清掉：它们是「实例的值」而非「本次按请求写的意图」，
    /// 否则构造后改 <c>Defaults.MaxRedirects</c> 会让每次调用都在校验层抛错。
    /// </para>
    /// </summary>
    private AxiosConfig NewEffectiveConfig()
    {
        var config = Defaults.Clone();
        config.EffectiveMerged = true;
        config.ClearInstanceOptionFlags();

        // 记下「哪些 transform 是继承来的」：合并层只对这一段去重（回放不重复执行默认 transform），
        // 回调里追加的项一律原样保留 —— 包括与默认链同引用的有意重复注册。
        // 走 Has*/Count 访问器：_transformRequest 是 AxiosConfig 的私有字段，跨类不可见。
        config.TransformInheritedRequest = Defaults.HasTransformRequest ? Defaults.TransformRequest.Count : 0;
        config.TransformInheritedResponse = Defaults.HasTransformResponse ? Defaults.TransformResponse.Count : 0;
        return config;
    }

    /// <summary>
    /// 应用 <see cref="AxiosConfig.RemoveHeaders"/>：生效配置里没有可并的头，
    /// 但「先并头、再摘掉要删除的」这条语义要求删除发生在 configure 写完头之后。
    /// </summary>
    private static void ApplyRemoveHeaders(AxiosConfig config)
    {
        if (config.HasHeaders && config.RemoveHeadersSnapshot is { } removeHeaders)
        {
            foreach (var name in removeHeaders)
            {
                config.Headers.Delete(name);
            }
        }
    }

    /// <summary>
    /// 媒体类型是否已经是一种<b>表单编码</b>（<c>application/x-www-form-urlencoded</c> 或 <c>multipart/form-data</c>，
    /// 后者允许带 <c>boundary</c> 等参数）。表单快捷方法只在这种默认值上让步。
    /// </summary>
    private static bool IsFormContentType(string contentType)
        => MediaTypes.Is(contentType, AxiosContentType.FormUrlEncoded)
            || MediaTypes.Is(contentType, AxiosContentType.MultipartFormData);

    /// <summary>把 url/method 填进调用方传入的配置，交给管线按常规路径合并到实例默认值。</summary>
    private AxiosConfig WithUrl(AxiosConfig? config, string url, HttpMethod method)
    {
        // PostAsync(url, null) 的重载决议会落到本方法的调用方（AxiosConfig 比 object 更具体）。
        // null 表示「没有要传的配置」，按普通快捷路径（空 data）处理 —— 与 PostAsync(url) 等价，
        // 而不是让一个很常见的「空正文 POST」写法变成 ArgumentNullException。
        if (config is null)
        {
            return Shortcut(url, method, null, default);
        }

        // 与快捷方法同一套 fail-fast：url 为 null / 空白时同样不能静默落到 baseAddress
        if (string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(
                url ?? string.Empty,
                config,
                message: "url 不能为 null 或空白：快捷方法的第一个参数是必填的请求地址"
                    + "（要请求 baseAddress 本身请显式传 \"/\"）。");
        }

        // 不能就地修改调用方传入的 config：并发复用会让 Url/Method 互相覆盖。
        // Clone() 同时清掉 EffectiveMerged，使 response.Config 这类生效配置重新参与合并。
        var copy = config.Clone();
        copy.Url = url;
        copy.Method = method;
        return copy;
    }
}
