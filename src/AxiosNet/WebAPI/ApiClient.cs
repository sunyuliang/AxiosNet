using System.Diagnostics.CodeAnalysis;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

/// <summary>声明式 API 客户端：请求模型描述「怎么访问」，授权描述「凭什么访问」。本类是 <see cref="AxiosClient"/> 的薄门面，请求模型绑定成 AxiosConfig 后走完整管线；深度定制直接取 <see cref="Axios"/>。</summary>
public sealed partial class ApiClient : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsAxiosClient;

    /// <summary>底层 AxiosNet 客户端（实例默认值、拦截器、连接池都从它进入）。</summary>
    public AxiosClient Axios { get; }

    /// <summary>授权提供方：每次请求发送前调用，把凭据写进最终配置；null 表示匿名访问。标注了 <see cref="AllowAnonymousAttribute"/> 的模型会跳过它。</summary>
    public IAuthorizationProvider? Authorization { get; set; }

    /// <summary>
    /// 访问令牌来源：在 <see cref="Authorization"/> 之前调用，按 <c>Authorization: {scheme} {token}</c> 写头
    /// （scheme 取 <see cref="AuthorizeAttribute"/>，默认 <c>Bearer</c>）。令牌为 null / 空白时不写头，模型已显式写该头时不覆盖。
    /// </summary>
    public IAccessTokenProvider? AccessTokenProvider { get; set; }

    /// <summary>流式请求正文的空闲超时，默认 <see cref="TimeSpan.Zero"/>（不限时，只受取消令牌约束）；大于零表示连续这么久没有收到数据才失败（<c>ECONNABORTED</c>）。</summary>
    public TimeSpan StreamIdleTimeout
    {
        get => _streamIdleTimeout;
        set
        {
            _streamIdleTimeout = value;
            _streamIdleTimeoutSet = true;
        }
    }

    private TimeSpan _streamIdleTimeout = TimeSpan.Zero;

    /// <summary>是否显式给 <see cref="StreamIdleTimeout"/> 赋过值（默认值本身代表不了调用方意图）。</summary>
    private bool _streamIdleTimeoutSet;

    /// <summary>URL 参数格式化钩子：绑定 <c>[Path]</c> 与 <c>[Query]</c> 的值时调用，返回的字符串直接作为该参数的文本（路径段仍会被 URL 转义）；返回 <c>null</c> 表示本次不处理，回落到默认规则。</summary>
    public Func<object, string?>? UrlParameterFormatter { get; set; }

    /// <summary>HTTP 状态码错误的异常工厂：非 null 时，状态码不合法的请求先交给它构造异常；返回 <c>null</c> 表示仍抛原始 <see cref="AxiosException"/>。内置实现见 <see cref="ApiErrorFactory{TError}"/>。</summary>
    public IApiErrorFactory? ErrorFactory { get; set; }

    /// <summary>以 baseAddress 创建自管的 AxiosClient。</summary>
    /// <param name="baseAddress">API 基地址（合并规则与 AxiosNet 的 BaseAddress 一致，请求模型可写绝对 URL 覆盖）。</param>
    /// <param name="configureDefaults">实例默认配置（超时、头、序列化器等），null 表示全默认。</param>
    public ApiClient(string baseAddress, Action<AxiosConfig>? configureDefaults = null)
    {
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new ArgumentException("baseAddress 不能为空白。", nameof(baseAddress));
        }

        var defaults = new AxiosConfig { BaseAddress = baseAddress };
        configureDefaults?.Invoke(defaults);
        Axios = new AxiosClient(defaults);
        _ownsAxiosClient = true;
    }

    /// <summary>包装既有 AxiosClient（拦截器 / 连接池 / 实例默认值已就绪的场景）。</summary>
    /// <param name="axiosClient">底层客户端。</param>
    /// <param name="ownsAxiosClient">释放本门面时是否一并释放 <paramref name="axiosClient"/>。</param>
    public ApiClient(AxiosClient axiosClient, bool ownsAxiosClient = false)
    {
        ArgumentNullException.ThrowIfNull(axiosClient);
        Axios = axiosClient;
        _ownsAxiosClient = ownsAxiosClient;
    }

    /// <summary>发送请求模型描述的请求，返回类型由请求模型的 <see cref="IApiRequest{TResponse}"/> 泛型确定。</summary>
    /// <exception cref="InvalidOperationException">请求模型定义非法（缺路由特性、占位符未绑定、多个请求体等）。</exception>
    public Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => RequestAsync(request, configure: null, cancellationToken);

    /// <summary>同 <see cref="RequestAsync{TResponse}(IApiRequest{TResponse}, CancellationToken)"/>，另支持按请求微调配置。</summary>
    /// <param name="request">请求模型实例。</param>
    /// <param name="configure">在参数绑定与 <see cref="IApiRequestConfigurable.Configure"/> 之后、授权之前调用。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
    public async Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        await ApplyAuthorizationAsync(metadata, request, config, cancellationToken).ConfigureAwait(false);

        return await SendAsync<TResponse>(request, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按请求模型拼出最终 URL（含路径替换、查询参数与 BaseAddress 合并），不发请求。可用于日志与签名；返回实际发送形态（已转义）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
    public string GetUri<TResponse>(IApiRequest<TResponse> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = RequestModelMetadata.Get(request.GetType());
        return Axios.GetUri(BuildConfig(metadata, request, configure: null, cancellationToken: default));
    }

    private AxiosConfig BuildConfig(
        RequestModelMetadata metadata,
        object request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        var config = new AxiosConfig
        {
            Method = metadata.Method,
            Url = metadata.ResolveUrl(request, UrlParameterFormatter)
        };

        // 没有查询参数时不写：合并层把显式 null 当成摘掉实例默认值。
        if (metadata.ResolveQuery(request, UrlParameterFormatter) is { } query)
        {
            config.Params = query;
        }

        var body = ResolveBody(metadata, request, config);
        if (body is not null)
        {
            config.Data = body;
        }

        // 类级静态头先写，属性上的 [Header] / [HeaderCollection] 覆盖同名项
        metadata.ApplyStaticHeaders(config);
        metadata.ApplyHeaders(request, config);

        // 仅在调用方传入可取消的令牌时才写入：None 会覆盖实例默认令牌
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        (request as IApiRequestConfigurable)?.Configure(config);
        configure?.Invoke(config);

        // [Multipart] 模型：Content-Type 由库设定（调用方/Configure 显式设置过则保留，可自带 boundary）
        if (metadata.IsMultipart && !config.ContentTypeSet)
        {
            config.ContentType = AxiosContentType.MultipartFormData;
        }

        return config;
    }

    /// <summary>取请求体（<c>[Body]</c> 的值 / <c>[Multipart]</c> 装配好的表单）；取值失败归 <c>ERR_BAD_REQUEST</c>。</summary>
    private static object? ResolveBody(RequestModelMetadata metadata, object request, AxiosConfig config)
    {
        try
        {
            return metadata.ResolveBody(request);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.BadRequestPayload(config, request: null, request.GetType().Name, ex);
        }
    }

    /// <summary>授权链路：<see cref="AllowAnonymousAttribute"/> 跳过；<see cref="AccessTokenProvider"/> 先写头，<see cref="Authorization"/> 最后执行（可覆盖前者）。</summary>
    private async ValueTask ApplyAuthorizationAsync(
        RequestModelMetadata metadata,
        object request,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (metadata.Authorization.Mode == ApiAuthorizationMode.Anonymous)
        {
            return;
        }

        var context = new ApiAuthContext(request, config);

        if (AccessTokenProvider is not null && !HasExplicitAuthorization(config))
        {
            var token = await AccessTokenProvider.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                config.Headers.Set("Authorization", metadata.Authorization.Scheme + " " + token);
            }
        }

        if (Authorization is not null)
        {
            await Authorization.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>模型或实例默认值里是否已经写了 <c>Authorization</c> 头（显式配置优先于令牌提供方）。</summary>
    private bool HasExplicitAuthorization(AxiosConfig config)
        => (config.HasHeaders && config.Headers.Has("Authorization"))
            || (Axios.Defaults.HasHeaders && Axios.Defaults.Headers.Has("Authorization"));

    /// <summary>发送请求；「服务端给了响应却是错误状态码」时交给 <see cref="ErrorFactory"/>。</summary>
    private async Task<AxiosResponse<TResponse>> SendAsync<TResponse>(
        object request,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Axios.RequestAsync<TResponse>(config).ConfigureAwait(false);
        }
        catch (AxiosException error) when (ErrorFactory is not null && error.Response is not null)
        {
            // 只接管「服务端给了响应」的错误；工厂返回 null 时原样再抛。
            var replacement = await ErrorFactory
                .CreateAsync(new ApiErrorContext(request, error), cancellationToken)
                .ConfigureAwait(false);

            if (replacement is not null)
            {
                throw replacement;
            }

            throw;
        }
    }

    /// <summary>释放底层 AxiosClient（仅当本门面拥有它）。</summary>
    public void Dispose()
    {
        if (_ownsAxiosClient)
        {
            Axios.Dispose();
        }
    }

    /// <summary>与 <see cref="Dispose"/> 等价；支持 <c>await using</c>。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
