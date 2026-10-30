using System.Diagnostics.CodeAnalysis;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

/// <summary>声明式 API 客户端：请求模型描述「怎么访问」，授权描述「凭什么访问」。本类是 <see cref="AxiosClient"/> 的薄门面，请求模型绑定成 AxiosConfig 后走完整管线；深度定制直接取 <see cref="Axios"/>。</summary>
public sealed partial class ApiClient : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsAxiosClient;

    /// <summary>底层 AxiosNet 客户端（实例默认值、拦截器、连接池都从它进入）。</summary>
    public AxiosClient Axios { get; }

    /// <summary>授权提供方：每次请求发送前调用，把凭据写进最终配置；null 表示匿名访问。标注了 <see cref="AllowAnonymousAttribute"/> 的声明会跳过它。</summary>
    public IAuthorizationProvider? Authorization { get; set; }

    /// <summary>
    /// 访问令牌来源：在 <see cref="Authorization"/> 之前调用，按 <c>Authorization: {scheme} {token}</c> 写头
    /// （scheme 取 <see cref="AuthorizeAttribute"/>，默认 <c>Bearer</c>）。令牌为 null / 空白时不写头，模型或实例默认值已显式写该头时不覆盖。
    /// </summary>
    public IAccessTokenProvider? AccessTokenProvider { get; set; }

    /// <summary>URL 参数格式化钩子：绑定 <c>[Path]</c> 与 <c>[Query]</c> 的值时调用，返回的字符串直接作为该参数的文本（路径段仍会被 URL 转义）；返回 <c>null</c> 表示本次不处理，回落到默认规则。</summary>
    public Func<object, string?>? UrlParameterFormatter { get; set; }

    /// <summary>
    /// 收到 401 时用新令牌重放一次，默认开启（只在 <see cref="AccessTokenProvider"/> 真的写了 <c>Authorization</c> 头时生效；
    /// 另配了 <see cref="Authorization"/> 时不重放，它可能覆盖该头）。
    /// 关掉后 401 直接按错误模型抛出。正文不可重放（流 / 流式请求体 / multipart）时一律不重放。
    /// </summary>
    public bool RetryOnUnauthorized { get; set; } = true;

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
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「Native AOT / 裁剪」）。")]
    public async Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        var tokenManaged = await ApplyAuthorizationAsync(metadata.Authorization, request, config, cancellationToken).ConfigureAwait(false);

        return await SendAsync<TResponse>(request, config, tokenManaged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按请求模型拼出最终 URL（含路径替换、查询参数与 BaseAddress 合并），不发请求。可用于日志与签名；返回实际发送形态（已转义）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「Native AOT / 裁剪」）。")]
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
    /// <returns><c>Authorization</c> 头是否由 <see cref="AccessTokenProvider"/> 写入（401 重放的判定依据）。</returns>
    internal async ValueTask<bool> ApplyAuthorizationAsync(
        ApiAuthorization authorization,
        object request,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (authorization.Mode == ApiAuthorizationMode.Anonymous)
        {
            return false;
        }

        var context = new ApiAuthContext(request, config);

        if (AccessTokenProvider is not null && !HasExplicitAuthorization(config))
        {
            var token = await AccessTokenProvider.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                config.Headers.Set("Authorization", authorization.Scheme + " " + token);

                // 自定义授权提供方随后可能覆盖这个头：那种情况不归令牌提供方管，401 也不重放。
                if (Authorization is null)
                {
                    return true;
                }
            }
        }

        if (Authorization is not null)
        {
            await Authorization.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>模型或实例默认值里是否已经写了 <c>Authorization</c> 头（显式配置优先于令牌提供方）。</summary>
    private bool HasExplicitAuthorization(AxiosConfig config)
        => (config.HasHeaders && config.Headers.Has("Authorization"))
            || (Axios.Defaults.HasHeaders && Axios.Defaults.Headers.Has("Authorization"));

    /// <summary>发送请求；「服务端给了响应却是错误状态码」时交给 <see cref="ErrorFactory"/>。</summary>
    internal Task<AxiosResponse<TResponse>> SendAsync<TResponse>(
        object request,
        AxiosConfig config,
        bool tokenManaged,
        CancellationToken cancellationToken)
        => GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, tokenManaged, cancellationToken, () => Axios.RequestAsync<TResponse>(config)));

    /// <summary>发送请求并交付非泛型响应（正文按 <see cref="System.Text.Json.JsonElement"/>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    internal Task<AxiosResponse> SendUntypedAsync(
        object request,
        AxiosConfig config,
        bool tokenManaged,
        CancellationToken cancellationToken)
        => GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, tokenManaged, cancellationToken, () => Axios.SendInternalAsync(config, dataType: null, target: null)));

    /// <summary>只完成请求、不装配正文（接口声明返回 <c>Task</c>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    internal async Task SendWithoutBodyAsync(
        object request,
        AxiosConfig config,
        bool tokenManaged,
        CancellationToken cancellationToken)
    {
        var response = await GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, tokenManaged, cancellationToken, () => Axios.SendInternalAsync(config, typeof(void), target: null)))
            .ConfigureAwait(false);

        // 显式把 responseType 配成 Stream 时正文归我们：Task 方法的调用方拿不到这个响应对象。
        // 短路响应（Request 为 null）的 Data 归调用方，不能释放。
        if (response.Request is not null)
        {
            (response.Data as IDisposable)?.Dispose();
        }
    }

    /// <summary>发送一次；401 且本次令牌由提供方写入时，刷新令牌后重放一次（重放同样只有一次）。</summary>
    private async Task<TResult> SendWithAuthRetryAsync<TResult>(
        object request,
        AxiosConfig config,
        bool tokenManaged,
        CancellationToken cancellationToken,
        Func<Task<TResult>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (AxiosException error) when (CanReplayForAuth(error, config, tokenManaged))
        {
            // 重放是同一个逻辑请求：沿用上次实际发出的幂等键。
            if (error.Config?.IdempotencyOrNull is { Enabled: true, GeneratedKey: { } key })
            {
                config.Idempotency.Key = key;
            }

            if (!await ReauthorizeAsync(request, config, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            return await send().ConfigureAwait(false);
        }
    }

    /// <summary>这次 401 值不值得重放：开关、令牌归属、状态码、正文可重放四项都满足。</summary>
    private bool CanReplayForAuth(AxiosException error, AxiosConfig config, bool tokenManaged)
        => RetryOnUnauthorized
            && tokenManaged
            && AccessTokenProvider is not null
            && error.Status == 401
            && IsReplayableBody(config);

    /// <summary>401 后重新取令牌并写回头；返回 <c>false</c> 表示没拿到新令牌（重放没有意义）。</summary>
    private async ValueTask<bool> ReauthorizeAsync(object request, AxiosConfig config, CancellationToken cancellationToken)
    {
        var previous = config.Headers.Get("Authorization");
        var scheme = ResolveScheme(previous);
        var staleToken = previous is not null && previous.Length > scheme.Length + 1
            ? previous[(scheme.Length + 1)..]
            : null;

        // 让管理器把当前令牌判为失效，下一次取令牌必然刷新。
        (AccessTokenProvider as IAccessTokenRefresher)?.Invalidate(staleToken);

        config.Headers.Delete("Authorization");

        var token = await AccessTokenProvider!.GetTokenAsync(new ApiAuthContext(request, config), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // 已含空格的令牌视为「scheme + 参数」完整形态，原样发送（与 BearerAuthorizationProvider 一致）。
        var value = token.Contains(' ') ? token : scheme + " " + token;
        config.Headers.Set("Authorization", value);

        return !string.Equals(value, previous, StringComparison.Ordinal);
    }

    /// <summary>从已有的 <c>Authorization</c> 头取 scheme；拿不到时按 <c>Bearer</c>。</summary>
    private static string ResolveScheme(string? authorization)
    {
        if (authorization is not null)
        {
            var separator = authorization.IndexOf(' ');
            if (separator > 0)
            {
                return authorization[..separator];
            }
        }

        return "Bearer";
    }

    /// <summary>
    /// 正文能否原样再发一次：只有「数据本身」能重新生成正文的才行。
    /// Stream / HttpContent 随第一次请求的消息一起被释放（模板在管线收尾时 Dispose），
    /// 流式正文与 multipart 的流部件也拿不到第二份，一律按不可重放。
    /// </summary>
    private static bool IsReplayableBody(AxiosConfig config)
        => config.Data switch
        {
            null => true,
            Stream => false,
            IAsyncEnumerable<byte[]> => false,
            IAsyncEnumerable<ReadOnlyMemory<byte>> => false,
            HttpContent => false,
            DeclaredForm => false,
            _ => true,
        };
    /// <summary>「服务端给了响应却是错误状态码」时交给 <see cref="ErrorFactory"/>；网络 / 超时 / 取消不经过工厂。</summary>
    private async Task<TResult> GuardAsync<TResult>(
        object request,
        CancellationToken cancellationToken,
        Func<Task<TResult>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
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
