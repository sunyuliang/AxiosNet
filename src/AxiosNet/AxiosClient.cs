using System.Text.Json;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// axios 风格的 HTTP 客户端实例，对应 JS 中的 axios instance。
/// 支持 baseURL / method / headers / params / data / timeout / auth / responseType /
/// validateStatus / 取消令牌 / 请求与响应拦截器，默认连接复用并由连接池统一管理。
/// </summary>
public sealed class AxiosClient : IDisposable
{
    private readonly HttpConnectionPool _pool;
    private readonly bool _ownsPool;
    private int _disposed;

    /// <summary>对应 axios instance.defaults。</summary>
    public AxiosConfig Defaults { get; }

    /// <summary>对应 axios instance.interceptors。</summary>
    public AxiosInterceptors Interceptors { get; } = new();

    /// <summary>该实例使用的连接池。</summary>
    public HttpConnectionPool Pool => _pool;

    public AxiosClient(
        AxiosConfig? defaults = null,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null)
    {
        Defaults = defaults ?? new AxiosConfig();

        if (pool is not null)
        {
            _pool = pool;
            _ownsPool = false;
        }
        else
        {
            var effective = (poolOptions ?? Defaults.PoolOptions ?? new ConnectionPoolOptions()).Clone();
            effective.MaxAutomaticRedirections = Defaults.MaxRedirects > 0
                ? Defaults.MaxRedirects
                : effective.MaxAutomaticRedirections;
            effective.Proxy ??= Defaults.Proxy;
            effective.Credentials ??= Defaults.Credentials;
            if (Defaults.WithCredentials)
            {
                effective.UseCookies = true;
                effective.Credentials ??= CredentialCache.DefaultCredentials;
            }

            _pool = new HttpConnectionPool(effective);
            _ownsPool = true;
        }
    }

    // ------------------------------------------------------------------
    // request(config) 入口
    // ------------------------------------------------------------------

    /// <summary>对应 axios(config)，返回非泛型响应（JSON 时 Data 为 JsonElement）。</summary>
    public Task<AxiosResponse> RequestAsync(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(config, dataType: null);
    }

    /// <summary>对应 axios(config)，Data 反序列化为 T。</summary>
    public async Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var response = await SendAsync(config, typeof(T)).ConfigureAwait(false);
        return response.AsTyped<T>();
    }

    /// <summary>用 Action 配置请求的非泛型入口。</summary>
    public Task<AxiosResponse> RequestAsync(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var config = new AxiosConfig();
        configure(config);
        return SendAsync(config, null);
    }

    /// <summary>用 Action 配置请求的泛型入口。</summary>
    public async Task<AxiosResponse<T>> RequestAsync<T>(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var config = new AxiosConfig();
        configure(config);
        var response = await SendAsync(config, typeof(T)).ConfigureAwait(false);
        return response.AsTyped<T>();
    }

    // ------------------------------------------------------------------
    // 快捷方法（与 axios.get/post/put/patch/delete/head/options 对齐）
    // ------------------------------------------------------------------

    public Task<AxiosResponse> GetAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Get, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> GetAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Get, configure, cancellationToken));

    /// <summary>GET 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> GetAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Get, configure, cancellationToken, data), null);

    /// <summary>GET 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> GetAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Get, configure, cancellationToken, data));

    public Task<AxiosResponse> GetAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, AxiosMethod.Get), null);

    public Task<AxiosResponse<T>> GetAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, AxiosMethod.Get));

    public Task<AxiosResponse> DeleteAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Delete, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Delete, configure, cancellationToken));

    /// <summary>DELETE 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> DeleteAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Delete, configure, cancellationToken, data), null);

    /// <summary>DELETE 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Delete, configure, cancellationToken, data));

    public Task<AxiosResponse> DeleteAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, AxiosMethod.Delete), null);

    public Task<AxiosResponse<T>> DeleteAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, AxiosMethod.Delete));

    public Task<AxiosResponse> HeadAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Head, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> HeadAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Head, configure, cancellationToken));

    /// <summary>HEAD 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> HeadAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Head, configure, cancellationToken, data), null);

    /// <summary>HEAD 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> HeadAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Head, configure, cancellationToken, data));

    public Task<AxiosResponse> OptionsAsync(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Options, configure, cancellationToken), null);

    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Options, configure, cancellationToken));

    /// <summary>OPTIONS 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse> OptionsAsync(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Options, configure, cancellationToken, data), null);

    /// <summary>OPTIONS 携带请求体。data 支持全部类型（对象/字典/dynamic/string/byte[]/Stream/HttpContent）。</summary>
    public Task<AxiosResponse<T>> OptionsAsync<T>(string url, object? data, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Options, configure, cancellationToken, data));

    public Task<AxiosResponse> PostAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Post, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PostAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Post, configure, cancellationToken, data));

    public Task<AxiosResponse> PostAsync(string url, AxiosConfig config)
        => SendAsync(WithUrl(config, url, AxiosMethod.Post), null);

    public Task<AxiosResponse<T>> PostAsync<T>(string url, AxiosConfig config)
        => SendTypedAsync<T>(WithUrl(config, url, AxiosMethod.Post));

    public Task<AxiosResponse> PutAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Put, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PutAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Put, configure, cancellationToken, data));

    public Task<AxiosResponse> PatchAsync(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendAsync(Shortcut(url, AxiosMethod.Patch, configure, cancellationToken, data), null);

    public Task<AxiosResponse<T>> PatchAsync<T>(string url, object? data = null, Action<AxiosConfig>? configure = null, CancellationToken cancellationToken = default)
        => SendTypedAsync<T>(Shortcut(url, AxiosMethod.Patch, configure, cancellationToken, data));

    private async Task<AxiosResponse<T>> SendTypedAsync<T>(AxiosConfig config)
    {
        var response = await SendAsync(config, typeof(T)).ConfigureAwait(false);
        return response.AsTyped<T>();
    }

    private static AxiosConfig Shortcut(
        string url,
        AxiosMethod method,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken,
        object? data = null)
    {
        var config = new AxiosConfig
        {
            Url = url,
            Method = method
        };

        // 仅在调用方传入可取消的令牌时才写入，避免用 CancellationToken.None 覆盖掉实例默认令牌
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }
        if (data is not null)
        {
            config.Data = data;
        }

        configure?.Invoke(config);
        return config;
    }

    private static AxiosConfig WithUrl(AxiosConfig config, string url, AxiosMethod method)
    {
        config.Url = url;
        config.Method = method;
        return config;
    }

    // ------------------------------------------------------------------
    // 核心请求管线
    // ------------------------------------------------------------------

    private async Task<AxiosResponse> SendAsync(AxiosConfig perCallConfig, Type? dataType)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var config = MergeConfig(perCallConfig);

        // 泛型调用 XxxAsync<dynamic>/<object> 时（T 即 object），响应自动展开为动态对象图，
        // 对标 JS 中直接 response.data.xxx 的用法；显式指定过其他 responseType 时以显式配置为准。
        if (dataType == typeof(object) && config.ResponseType == ResponseType.Json)
        {
            config.ResponseType = ResponseType.Dynamic;
        }

        // 1) 请求拦截器：与 axios 一致，按注册逆序执行
        foreach (var interceptor in Interceptors.Request.SnapshotReversed())
        {
            try
            {
                config = await interceptor.OnFulfilled(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (interceptor.OnRejected is null)
                {
                    throw;
                }

                config = await interceptor.OnRejected(ex).ConfigureAwait(false);
            }
        }

        // 2) 构造 URI 与 HttpRequestMessage
        var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params);
        var request = new HttpRequestMessage(config.Method.ToHttpMethod(), uri)
        {
            Version = config.HttpVersion ?? HttpVersion.Version11
        };

        var content = RequestBodyFactory.Create(config.Data, config);
        if (content is not null)
        {
            request.Content = content;
        }

        ApplyHeaders(request, config);

        var externalToken = config.CancellationToken;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        if (config.Timeout > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(config.Timeout);
        }

        var streamResponse = config.ResponseType == ResponseType.Stream;

        // 3) 真正的发送 + 读取 + 状态码校验（异常会进入响应拦截器 rejected 链）
        Func<Task<AxiosResponse>> invoker = async () =>
        {
            PoolLease? lease = null;
            try
            {
                lease = _pool.Lease(uri);

                HttpResponseMessage responseMessage;
                try
                {
                    responseMessage = await lease.Client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!externalToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
                    {
                        throw AxiosException.Timeout(config, request);
                    }

                    throw AxiosException.Canceled(config, request);
                }
                catch (HttpRequestException ex)
                {
                    throw AxiosException.Network(config, request, ex);
                }

                AxiosResponse response;
                try
                {
                    response = await ReadResponseAsync(responseMessage, request, config, linkedCts.Token, dataType)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    responseMessage.Dispose();
                    if (!externalToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
                    {
                        throw AxiosException.Timeout(config, request);
                    }

                    throw AxiosException.Canceled(config, request);
                }

                // stream 模式下消息不能释放（调用方要继续读流）
                if (!streamResponse)
                {
                    responseMessage.Dispose();
                }

                var validate = config.ValidateStatus ?? DefaultValidateStatus;
                if (!validate(response.Status))
                {
                    throw AxiosException.FromResponse(config, request, response);
                }

                return response;
            }
            finally
            {
                lease?.Dispose();
            }
        };

        // 4) 响应拦截器：按注册顺序包装成责任链
        var chain = invoker;
        foreach (var interceptor in Interceptors.Response.Snapshot())
        {
            var previous = chain;
            var fulfilled = interceptor.OnFulfilled;
            var rejected = interceptor.OnRejected;
            chain = async () =>
            {
                AxiosResponse response;
                try
                {
                    response = await previous().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (rejected is null)
                    {
                        throw;
                    }

                    return await rejected(ex).ConfigureAwait(false);
                }

                return await fulfilled(response).ConfigureAwait(false);
            };
        }

        try
        {
            return await chain().ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static bool DefaultValidateStatus(int status) => status is >= 200 and < 300;

    private static async Task<AxiosResponse> ReadResponseAsync(
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config,
        CancellationToken cancellationToken,
        Type? dataType)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
        {
            headers[header.Key] = header.Value;
        }

        foreach (var header in message.Content.Headers)
        {
            headers[header.Key] = header.Value;
        }

        object? data;
        if (config.ResponseType == ResponseType.Stream)
        {
            data = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 原始内容优先：一律先取原始字节，再解码为文本，最后按返回类型转换。
            var bytes = await message.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var text = ResponseTextDecoder.Decode(
                bytes,
                message.Content.Headers.ContentType?.CharSet,
                config.ResponseEncoding);
            data = ConvertData(config, request, dataType, bytes, text);
        }

        var status = (int)message.StatusCode;
        return new AxiosResponse
        {
            Data = data,
            Status = status,
            StatusText = StatusTextMap.Get(status),
            Headers = headers,
            Config = config,
            Request = request
        };
    }

    /// <summary>
    /// 把「原始字节 + 已解码文本」按 responseType / 目标类型转换为 Data。
    /// </summary>
    private static object? ConvertData(AxiosConfig config, HttpRequestMessage request, Type? dataType, byte[] bytes, string text)
    {
        // 1) 显式 responseType 优先：arraybuffer 直接给原始字节。
        if (config.ResponseType == ResponseType.ArrayBuffer)
        {
            return bytes;
        }

        // 2) text 直接给原始文本（不做任何 JSON 解析）。
        if (config.ResponseType == ResponseType.Text)
        {
            return text;
        }

        // 3) dynamic 展开为动态对象图（空 body 为 null，对齐 axios）。
        if (config.ResponseType == ResponseType.Dynamic)
        {
            return text.Length == 0 ? null : DynamicJson.Parse(text);
        }

        // 4) 目标类型就是原始内容本身：string 原样返回（支持纯文本），byte[] 直接给字节。
        if (dataType == typeof(string))
        {
            return text;
        }

        if (dataType == typeof(byte[]))
        {
            return bytes;
        }

        // 5) Json（默认）：空 body 视为 null。
        if (text.Length == 0)
        {
            return null;
        }

        if (dataType is null || dataType == typeof(object) || dataType == typeof(JsonElement))
        {
            // 对齐 axios transformResponse：JSON 解析失败时原样返回文本。
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return text;
            }
        }

        try
        {
            return JsonSerializer.Deserialize(
                text,
                dataType,
                config.JsonSerializerOptions ?? JsonOptions.Web);
        }
        catch (JsonException ex)
        {
            // 非 JSON 的裸标量（42 / 2024-01-01 / true …）：按目标类型解析原始文本。
            if (ScalarParser.TryParse(text, dataType, out var scalar))
            {
                return scalar;
            }

            // 兜底失败：包装成 AxiosException，保持统一的错误模型（而不是裸抛 JsonException）。
            throw AxiosException.BadResponse(config, request, dataType!, ex);
        }
    }

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
        merged.Headers.MergeFrom(perCall.Headers);
        if (perCall.Params is not null)
        {
            merged.Params = perCall.Params;
        }

        if (perCall.Data is not null)
        {
            merged.Data = perCall.Data;
        }

        merged.ContentType = perCall.ContentTypeSet ? perCall.ContentType : merged.ContentType;

        if (perCall.Timeout > TimeSpan.Zero)
        {
            merged.Timeout = perCall.Timeout;
        }

        merged.Auth = perCall.Auth ?? merged.Auth;
        // 只在显式设置时覆盖，否则沿用实例默认（否则 new AxiosConfig() 的默认值会把实例默认吞掉）
        merged.ResponseType = perCall.ResponseTypeSet ? perCall.ResponseType : merged.ResponseType;
        merged.ValidateStatus = perCall.ValidateStatus ?? merged.ValidateStatus;
        merged.CancellationToken = perCall.CancellationTokenSet ? perCall.CancellationToken : merged.CancellationToken;
        merged.HttpVersion = perCall.HttpVersion ?? merged.HttpVersion;
        merged.Proxy = perCall.Proxy ?? merged.Proxy;
        merged.Credentials = perCall.Credentials ?? merged.Credentials;
        // WithCredentials / MaxRedirects 属于实例级配置（构造 AxiosClient 时应用到连接池 Handler），
        // 无法按请求生效，因此这里不做合并，避免出现「设置了却无效果」的假象。
        merged.JsonSerializerOptions = perCall.JsonSerializerOptions ?? merged.JsonSerializerOptions;
        merged.RequestEncoding = perCall.RequestEncoding ?? merged.RequestEncoding;
        merged.ResponseEncoding = perCall.ResponseEncoding ?? merged.ResponseEncoding;
        merged.PoolOptions = perCall.PoolOptions ?? merged.PoolOptions;
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
            if (string.IsNullOrEmpty(header.Value))
            {
                continue;
            }

            if (content is not null && ContentHeaderNames.Contains(header.Key))
            {
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsPool)
        {
            _pool.Dispose();
        }
    }
}
