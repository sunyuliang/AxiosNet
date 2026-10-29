using AxiosNet.Internal;

namespace AxiosNet;

// AxiosClient 的快捷方法层：与 axios.get/post/put/patch/delete/head/options 及 postForm/putForm/patchForm
// 一一对应，只负责组装 AxiosConfig。
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

    public Task<AxiosResponse> HeadAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Head), null);

    public Task<AxiosResponse<T>> HeadAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Head));

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

    public Task<AxiosResponse> OptionsAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Options), null);

    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Options));

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

    public Task<AxiosResponse> PutAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Put), null);

    public Task<AxiosResponse<T>> PutAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Put));

    public Task<AxiosResponse> PatchAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, HttpMethod.Patch, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PatchAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, HttpMethod.Patch, configure, cancellationToken, data));

    public Task<AxiosResponse> PatchAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, HttpMethod.Patch), null);

    public Task<AxiosResponse<T>> PatchAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, HttpMethod.Patch));

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
    // CancellationToken 显式重载（令牌不再走 data 参数）。
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

    /// <summary>表单快捷方法入口：默认 multipart/form-data，实例默认已是表单编码时保留实例值。</summary>
    private AxiosConfig FormShortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data)
        => Shortcut(url, method, configure, cancellationToken, data, AxiosContentType.MultipartFormData);

    private async Task<AxiosResponse<T>> SendTypedAsync<T>(AxiosConfig config)
    {
        // 响应对象按目标类型预先造好，由管线就地填充。
        var typed = new AxiosResponse<T>();
        var response = await SendAsync(config, typeof(T), typed).ConfigureAwait(false);

        // 拦截器换掉了响应对象：走复制这条路径。
        if (!ReferenceEquals(response, typed))
        {
            return AsTyped<T>(response);
        }

        // Data 必须是 T：不匹配时报 ERR_BAD_RESPONSE。
        try
        {
            DataCast.EnsureCompatible<T>(typed.RawData, typed.Config, typed.Request);
        }
        catch
        {
            // 正文交不到调用方手里：流式正文必须在此释放，短路响应（Request == null）除外。
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
            // 泛型参数与 responseType 不匹配（如 stream + GetAsync<byte[]>）：流式正文必须在此释放，
            // 短路响应除外（Data 归调用方）。
            if (response.Request is not null)
            {
                (response.Data as IDisposable)?.Dispose();
            }

            response.Data = null;
            throw;
        }
    }

    /// <summary>组装一次快捷调用的<b>生效配置</b>：在实例默认值的副本上填写本次请求项，并标记 <see cref="AxiosConfig.EffectiveMerged"/>。</summary>
    private AxiosConfig Shortcut(
        string url,
        HttpMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data = null,
        string? defaultContentType = null)
    {
        var config = NewEffectiveConfig();

        // url 必填：null / 空白直接报错，要请求 baseAddress 请显式传 "/"。
        if (string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(url ?? string.Empty, config, message: "url 不能为 null 或空白：快捷方法的第一个参数是必填的请求地址" + "（要请求 baseAddress 本身请显式传 \"/\"）。");
        }

        config.Url = url;
        config.Method = method;

        // 表单快捷方法：configure > 方法默认 > 实例默认值；实例默认已是表单编码时保留。
        if (defaultContentType is not null && !IsFormContentType(config.ContentType))
        {
            config.ContentType = defaultContentType;
        }

        // 仅在令牌可取消时写入，避免覆盖实例默认令牌。
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        // 令牌落到 data 上时直接报错。
        if (data is CancellationToken)
        {
            throw AxiosException.InvalidOption("CancellationToken 不能作为请求体传入，请使用 cancellationToken 参数。", config);
        }

        if (data is not null)
        {
            config.Data = data;
        }

        configure?.Invoke(config);
        ApplyRemoveHeaders(config);

        return config;
    }

    /// <summary>本次请求生效配置的起点：实例默认值的副本，已标记 <see cref="AxiosConfig.EffectiveMerged"/> 并清掉实例级选项标记。</summary>
    private AxiosConfig NewEffectiveConfig()
    {
        var config = Defaults.Clone();
        config.EffectiveMerged = true;
        config.ClearInstanceOptionFlags();

        // 记下继承来的 transform 段长度：合并层只对这一段去重。
        config.TransformInheritedRequest = Defaults.HasTransformRequest ? Defaults.TransformRequest.Count : 0;
        config.TransformInheritedResponse = Defaults.HasTransformResponse ? Defaults.TransformResponse.Count : 0;
        return config;
    }

    /// <summary>按 <see cref="AxiosConfig.RemoveHeaders"/> 删头，必须在 configure 写完头之后执行。</summary>
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

    /// <summary>媒体类型是否已是表单编码（<c>application/x-www-form-urlencoded</c> 或 <c>multipart/form-data</c>，后者可带 <c>boundary</c> 等参数）。</summary>
    private static bool IsFormContentType(string contentType)
        => MediaTypes.Is(contentType, AxiosContentType.FormUrlEncoded)
            || MediaTypes.Is(contentType, AxiosContentType.MultipartFormData);

    /// <summary>把 url/method 填进调用方传入的配置，交给管线按常规路径合并到实例默认值。</summary>
    private AxiosConfig WithUrl(AxiosConfig? config, string url, HttpMethod method)
    {
        // null 表示未传配置，按普通快捷路径处理。
        if (config is null)
        {
            return Shortcut(url, method, null, default);
        }

        // 与快捷方法同一套校验：url 为 null / 空白直接报错。
        if (string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(url ?? string.Empty, config, message: "url 不能为 null 或空白：快捷方法的第一个参数是必填的请求地址" + "（要请求 baseAddress 本身请显式传 \"/\"）。");
        }

        // 不就地修改调用方传入的 config；Clone() 同时清掉 EffectiveMerged。
        var copy = config.Clone();
        copy.Url = url;
        copy.Method = method;
        return copy;
    }
}
