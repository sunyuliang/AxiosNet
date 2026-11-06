using System.Diagnostics.CodeAnalysis;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

/// <summary>声明式 API 客户端：请求模型描述「怎么访问」，授权描述「凭什么访问」。本类是 <see cref="AxiosClient"/> 的薄门面，请求模型绑定成 AxiosConfig 后走完整管线；深度定制直接取 <see cref="Axios"/>。</summary>
public sealed partial class ApiClient : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsAxiosClient;

    /// <summary>底层 AxiosNet 客户端（实例默认值、拦截器、连接池都从它进入）。</summary>
    public AxiosClient Axios { get; }

    /// <summary>凭据提供方：每次请求发送前调用一次，返回的凭据描述决定怎么落头；null 表示匿名访问。<see cref="AllowAnonymousAttribute"/> 声明会跳过它。</summary>
    public IApiCredentialProvider? CredentialProvider { get; set; }

    /// <summary>
    /// 收到 401 时用新凭据重放一次，默认开启（只在提供方返回托管凭据、由库写入 <c>Authorization</c> 头时生效）。
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
    /// <param name="request">请求模型实例（路径 / 查询 / 头 / 正文的取值来源）。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
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
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public async Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        var credential = await ApplyAuthorizationAsync(metadata.Authorization, request, config, cancellationToken).ConfigureAwait(false);

        return await SendAsync<TResponse>(request, config, credential.Kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按请求模型拼出最终 URL（含路径替换、查询参数与 BaseAddress 合并），不发请求。可用于日志与签名；返回实际发送形态（已转义）。</summary>
    /// <param name="request">请求模型实例；<typeparamref name="TResponse"/> 只决定响应类型，不参与 URL。</param>
    /// <returns>拼好并转义后的绝对 URL。</returns>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public string GetUri<TResponse>(IApiRequest<TResponse> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure: null, cancellationToken: default);
        return BindingErrors.Run(config, request.GetType().Name, () => Axios.GetUri(config));
    }

    /// <summary>按请求模型的声明拼出这次请求的配置：路由、静态头、绑定参数与授权都在这条路径上落地。</summary>
    private AxiosConfig BuildConfig(
        RequestModelMetadata metadata,
        object request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        // 格式化钩子只有一个来源（客户端的实例默认值）：绑定参数时就要用它，所以这里先取定，再写进本次配置供发送期的表单 / multipart 使用。
        var formatter = Axios.Defaults.Serialization.ValueText;
        var target = request.GetType().Name;
        var config = new AxiosConfig
        {
            Method = metadata.Method,
            Serialization = { ValueText = formatter }
        };

        // 声明式装配失败统一归 ERR_BAD_REQUEST（路径 / 查询 / 头 / 正文同口径）。
        config.Url = BindingErrors.Run(config, target, () => metadata.ResolveUrl(request, formatter));

        // 没有查询参数时不写：合并层把显式 null 当成摘掉实例默认值。
        if (BindingErrors.Run(config, target, () => metadata.ResolveQuery(request, formatter)) is { } query)
        {
            config.Params = query;
        }

        if (BindingErrors.Run(config, target, () => metadata.ResolveBody(request, formatter)) is { } body)
        {
            config.Data = body;
        }

        // 类级静态头先写，属性上的 [Header] / [HeaderCollection] 覆盖同名项
        BindingErrors.Run(config, target, () => metadata.ApplyStaticHeaders(config));
        BindingErrors.Run(config, target, () => metadata.ApplyHeaders(request, config, formatter));

        // 仅在调用方传入可取消的令牌时才写入：None 会覆盖实例默认令牌
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        (request as IApiRequestConfigurable)?.Configure(config);
        configure?.Invoke(config);

        // 钩子在绑定期就已经用过（路径 / 查询 / 头 / 正文全部定型），这里再换只会让同一个请求出现两个钩子：
        // 一路是「已经写进 URL 与头的文本」，一路是「发送期的表单 / multipart / 正文」。报错，不留静默半生效。
        if (!ReferenceEquals(config.Serialization.ValueText, formatter))
        {
            throw AxiosException.InvalidOption(
                $"请求模型 {target} 在绑定期之后修改了 Serialization.ValueText：路径 / 查询 / 请求头 / 正文已按原钩子定型，改了不会重算。"
                + "请把钩子设在客户端实例默认值上（AxiosClient.Defaults.Serialization.ValueText）；"
                + "直接调用（GetAsync / PostAsync 等）的按请求配置不受此限制。",
                config);
        }

        // [Multipart] 模型：Content-Type 由库设定（调用方/Configure 显式设置过则保留，可自带 boundary）
        if (metadata.IsMultipart && !config.ContentTypeSet)
        {
            config.ContentType = AxiosContentType.MultipartFormData;
        }

        return config;
    }

    /// <summary>授权链路：<see cref="AllowAnonymousAttribute"/> 跳过；显式 <c>Authorization</c> 头优先（不调用提供方）；托管凭据由库写头。</summary>
    /// <param name="authorization">声明的授权策略（模式与 scheme）。</param>
    /// <param name="request">请求载荷（模型实例或 <see cref="ApiInterfaceInvocation"/>），原样交给提供方。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <returns>库实际写入的凭据；<see cref="ApiCredential.None"/> 表示本次没写头（也就没有 401 重放的余地）。</returns>
    internal async ValueTask<ApiCredential> ApplyAuthorizationAsync(
        ApiAuthorization authorization,
        object request,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (authorization.Mode == ApiAuthorizationMode.Anonymous
            || CredentialProvider is null
            || HasExplicitAuthorization(config))
        {
            return ApiCredential.None;
        }

        var credential = await CredentialProvider
            .GetCredentialAsync(new ApiAuthContext(request, config), cancellationToken).ConfigureAwait(false);

        // 只有托管形态（Token / Header）由库写头：Custom 是「提供方已自己写好」，None 是「本次不带」。
        if (credential.Kind is not (ApiCredentialKind.Token or ApiCredentialKind.Header)
            || string.IsNullOrWhiteSpace(credential.Value))
        {
            return ApiCredential.None;
        }

        config.Headers.Set("Authorization", BuildHeaderValue(credential, authorization.Scheme));
        return credential;
    }

    /// <summary>按凭据形态拼出 <c>Authorization</c> 头值。</summary>
    /// <param name="credential">库要写入的凭据（<see cref="ApiCredentialKind.Token"/> 或 <see cref="ApiCredentialKind.Header"/>）。</param>
    /// <param name="scheme">令牌形态要拼的 scheme（<c>null</c> 时按 <c>Bearer</c>）；完整头值形态忽略它。</param>
    internal static string BuildHeaderValue(ApiCredential credential, string? scheme)
        => credential.Kind == ApiCredentialKind.Header
            ? credential.Value!
            : (scheme ?? "Bearer") + " " + credential.Value;

    /// <summary>模型或实例默认值里是否已经写了 <c>Authorization</c> 头：写了就整条凭据链路都不进（显式配置优先）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    private bool HasExplicitAuthorization(AxiosConfig config)
        => (config.HasHeaders && config.Headers.Has("Authorization"))
            || (Axios.Defaults.HasHeaders && Axios.Defaults.Headers.Has("Authorization"));

    /// <summary>发送请求；「服务端给了响应却是错误状态码」时交给 <see cref="ErrorFactory"/>。</summary>
    /// <param name="request">请求载荷。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">授权链路实际写入的凭据形态（401 重放的判定依据）。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    internal Task<AxiosResponse<TResponse>> SendAsync<TResponse>(
        object request,
        AxiosConfig config,
        ApiCredentialKind credentialKind,
        CancellationToken cancellationToken)
        => GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, credentialKind, cancellationToken, () => Axios.RequestAsync<TResponse>(config)));

    /// <summary>发送请求并交付非泛型响应（正文按 <see cref="System.Text.Json.JsonElement"/>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    /// <param name="request">请求载荷。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">授权链路实际写入的凭据形态。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    internal Task<AxiosResponse> SendUntypedAsync(
        object request,
        AxiosConfig config,
        ApiCredentialKind credentialKind,
        CancellationToken cancellationToken)
        => GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, credentialKind, cancellationToken, () => Axios.SendInternalAsync(config, dataType: null, target: null)));

    /// <summary>只完成请求、不装配正文（接口声明返回 <c>Task</c>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    /// <param name="request">请求载荷。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">授权链路实际写入的凭据形态。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    internal async Task SendWithoutBodyAsync(
        object request,
        AxiosConfig config,
        ApiCredentialKind credentialKind,
        CancellationToken cancellationToken)
    {
        var response = await GuardAsync(request, cancellationToken, () => SendWithAuthRetryAsync(request, config, credentialKind, cancellationToken, () => Axios.SendInternalAsync(config, typeof(void), target: null)))
            .ConfigureAwait(false);

        // 显式把 responseType 配成 Stream 时正文归我们：Task 方法的调用方拿不到这个响应对象。
        // 短路响应（Request 为 null）的 Data 归调用方，不能释放。
        if (response.Request is not null)
        {
            (response.Data as IDisposable)?.Dispose();
        }
    }

    /// <summary>发送一次；401 且本次凭据由库写入时，刷新令牌后重放一次（重放同样只有一次）。</summary>
    /// <param name="request">请求载荷。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">授权链路实际写入的凭据形态。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <param name="send">真正发送的委托（重放时再调一次）。</param>
    private async Task<TResult> SendWithAuthRetryAsync<TResult>(
        object request,
        AxiosConfig config,
        ApiCredentialKind credentialKind,
        CancellationToken cancellationToken,
        Func<Task<TResult>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (AxiosException error) when (CanReplayForAuth(error, config, credentialKind))
        {
            // 重放是同一个逻辑请求：沿用上次实际发出的幂等键。
            if (error.Config?.IdempotencyOrNull is { Enabled: true, GeneratedKey: { } key })
            {
                config.Resilience.Idempotency.Key = key;
            }

            if (!await ReauthorizeAsync(request, config, credentialKind, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            return await send().ConfigureAwait(false);
        }
    }

    /// <summary>这次 401 值不值得重放：开关、凭据归属、状态码、正文可重放四项都满足。</summary>
    /// <param name="error">本次失败。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">授权链路实际写入的凭据形态。</param>
    private bool CanReplayForAuth(AxiosException error, AxiosConfig config, ApiCredentialKind credentialKind)
        => RetryOnUnauthorized
            && credentialKind is ApiCredentialKind.Token or ApiCredentialKind.Header
            && error.Status == 401
            && IsReplayableBody(config);

    /// <summary>401 后重新取凭据并写回头；返回 <c>false</c> 表示没拿到新值（重放没有意义）。</summary>
    /// <param name="request">请求载荷。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="credentialKind">上次由库写入的凭据形态。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    private async ValueTask<bool> ReauthorizeAsync(
        object request,
        AxiosConfig config,
        ApiCredentialKind credentialKind,
        CancellationToken cancellationToken)
    {
        var previousValue = config.Headers.Get("Authorization");

        // 令牌形态的头一定按「scheme + 空格 + 令牌」写出，反解才有确定性；
        // 完整头值形态拿不到令牌本体，按无条件作废（传 null），重拼时也不再加 scheme。
        string? scheme = null;
        string? staleToken = null;
        if (credentialKind == ApiCredentialKind.Token && previousValue is not null)
        {
            scheme = ResolveScheme(previousValue);
            staleToken = previousValue.Length > scheme.Length + 1 ? previousValue[(scheme.Length + 1)..] : null;
        }

        CredentialProvider!.Invalidate(staleToken);

        config.Headers.Delete("Authorization");

        var credential = await CredentialProvider
            .GetCredentialAsync(new ApiAuthContext(request, config), cancellationToken).ConfigureAwait(false);
        if (credential.Kind is not (ApiCredentialKind.Token or ApiCredentialKind.Header)
            || string.IsNullOrWhiteSpace(credential.Value))
        {
            return false;
        }

        var value = BuildHeaderValue(credential, scheme);
        config.Headers.Set("Authorization", value);

        return !string.Equals(value, previousValue, StringComparison.Ordinal);
    }

    /// <summary>从已有的 <c>Authorization</c> 头取 scheme；拿不到时按 <c>Bearer</c>。</summary>
    /// <param name="authorization">上次实际发出的头值。</param>
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
            FormData => false,
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
        catch (AxiosException error) when (error.IsRequestConstructionFailure)
        {
            // 查询展平阶段的取值 / 格式化失败：WebAPI 口径是「请求装配失败」，改判 ERR_BAD_REQUEST，原因保留原始异常。
            throw RequestBindingErrors.Create(
                error.Config,
                request: null,
                InvocationTargetName(request),
                error.Cause ?? error);
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

    /// <summary>错误消息里的目标名：请求模型类型名，或接口调用的 <c>接口.方法</c>。</summary>
    private static string InvocationTargetName(object request)
        => request is ApiInterfaceInvocation invocation
            ? $"{invocation.InterfaceType.Name}.{invocation.MethodName}"
            : request.GetType().Name;

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
