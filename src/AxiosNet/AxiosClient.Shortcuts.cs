using AxiosNet.Internal;

namespace AxiosNet;

// AxiosClient 的快捷方法层：与 axios.get/post/put/patch/delete/head/options
// 以及 postForm/putForm/patchForm 一一对应的重载，只负责组装 AxiosConfig。
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
    // CancellationToken 是 struct，没有这些重载时 GetAsync(url, ct) 会被装箱成 object
    // 落到 data 上，把令牌当请求体序列化。
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

    private AxiosConfig FormShortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data)
        => Shortcut(url, method, configure, cancellationToken, data, AxiosContentType.MultipartFormData);

    private async Task<AxiosResponse<T>> SendTypedAsync<T>(AxiosConfig config)
    {
        // 目标类型的响应对象在这里造好、交给管线就地填充：原先「管线造一个非泛型响应 →
        // AsTyped 复制成泛型响应」是每请求两个响应对象（约 120 B），而前者从不离开管线。
        var typed = new AxiosResponse<T>();
        var response = await SendAsync(config, typeof(T), typed).ConfigureAwait(false);

        // 响应拦截器把对象换掉了（它写不出具体的泛型参数，只能给回非泛型响应）：走复制这条老路径。
        if (!ReferenceEquals(response, typed))
        {
            return AsTyped<T>(response);
        }

        // Data 必须是 T：responseType 与泛型参数不匹配时给 ERR_BAD_RESPONSE，
        // 而不是把检查推迟到 Data getter（getter 抛异常会让日志、调试器、序列化统统在属性读取处炸掉）。
        try
        {
            DataCast.EnsureCompatible<T>(typed.RawData, typed.Config, typed.Request);
        }
        catch
        {
            // 这个响应已经交不到调用方手里了：流式正文必须在这里释放 ——
            // 否则池租约永不归还，该 origin 的连接会被一个没人读的流一直占住。
            (typed.RawData as IDisposable)?.Dispose();
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
            // 泛型参数与 responseType 不匹配（如 responseType: stream + GetAsync<byte[]>）：
            // 这个响应已经交不到调用方手里了，流式正文必须在这里释放 ——
            // 否则池租约永不归还，该 origin 的连接会被一个没人读的流一直占住。
            (response.Data as IDisposable)?.Dispose();
            response.Data = null;
            throw;
        }
    }

    /// <summary>
    /// 组装一次快捷调用的<b>生效配置</b>：直接在实例默认值的副本上填写本次请求项。
    /// <para>
    /// 以前这里是「先 new 一个只含按请求项的 AxiosConfig，再交给管线逐属性合并到 Defaults 副本」——
    /// 每请求两个配置对象（约 670 B）加一次全字段拷贝，而这两个对象里没有一个会活到请求之外。
    /// 现在只剩管线真正要用的那一个，<see cref="AxiosConfig.EffectiveMerged"/> 让管线知道不必再合并。
    /// </para>
    /// <para>
    /// 唯一的行为差异：<paramref name="configure"/> 回调里读到的 config 已经带上实例默认值
    /// （以前是一张白纸）。设置项不受影响 —— 写进副本与「写进按请求项再合并」结果相同。
    /// </para>
    /// </summary>
    private AxiosConfig Shortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data = null,
        string? defaultContentType = null)
    {
        var config = Defaults.Clone();
        config.EffectiveMerged = true;
        config.Url = url;
        config.Method = method;

        // 表单快捷方法：没人表态时默认 multipart。放在 configure 之前，回调里显式设的值仍然优先。
        if (defaultContentType is not null && !config.ContentTypeSet)
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

        // 合并语义是「先把按请求的头并进来，再摘掉要删除的」：这里没有可并的了，
        // 但 configure 里写的 RemoveHeaders 仍必须在它写完之后才生效。
        if (config.HasRemoveHeaders && config.HasHeaders)
        {
            foreach (var name in config.RemoveHeaders)
            {
                config.Headers.Delete(name);
            }
        }

        return config;
    }

    /// <summary>
    /// 把 url/method 填进调用方传入的配置，交给管线按常规路径合并到实例默认值。
    /// </summary>
    private static AxiosConfig WithUrl(AxiosConfig config, string url, HttpMethod method)
    {
        // PostAsync(url, null) 在重载决议里会落到本方法的调用方（AxiosConfig 比 object 更具体），
        // 没有这层守卫就是一句 NullReferenceException；这里给出明确的参数错误。
        ArgumentNullException.ThrowIfNull(config);

        // 不能就地修改调用方传入的 config：同一个 config 并发复用时 Url/Method 会互相覆盖。
        // Clone() 同时会清掉 EffectiveMerged：传入的是 response.Config 这类「上一次的生效配置」时，
        // 它会被当成按请求项重新合并到本实例默认值上（否则新实例的默认值会被整份跳过）。
        var copy = config.Clone();
        copy.Url = url;
        copy.Method = method;
        return copy;
    }
}
