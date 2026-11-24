using System.Diagnostics.CodeAnalysis;
using AxiosNet.Metadata;
using AxiosNet.Retry;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI;

/// <summary>声明式 API 客户端：请求模型描述「怎么访问」，授权描述「凭什么访问」；本类是 <see cref="AxiosClient"/> 的薄门面。</summary>
public sealed partial class ApiClient : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsAxiosClient;

    private readonly ApiCredentialCache _credentialCache = new();

    /// <summary>底层 AxiosNet 客户端（实例默认值、拦截器、连接池都从它进入）。</summary>
    public AxiosClient Axios { get; }

    /// <summary>凭据提供方：每次请求发送前调用一次，<c>null</c> 表示匿名访问；与显式 <c>Authorization</c> 头或 <c>Transport.Auth</c> 同时设置时报配置冲突。</summary>
    public IApiCredentialProvider? CredentialProvider { get; set; }

    /// <summary>收到 401 时用新凭据重放一次，默认 <c>true</c>（仅托管凭据生效）；流与 multipart 不重放，非幂等方法需打开 <c>Resilience.AllowUnsafeRetry</c>。</summary>
    public bool RetryOnUnauthorized { get; set; } = true;

    /// <summary>HTTP 状态码错误的异常工厂：非 <c>null</c> 时状态码不合法的请求先交给它构造异常；返回 <c>null</c> 表示仍抛原始 <see cref="AxiosException"/>。</summary>
    public IApiErrorFactory? ErrorFactory { get; set; }

    /// <summary>以 baseAddress 创建自管的 AxiosClient。</summary>
    /// <param name="baseAddress">API 基地址（合并规则与 AxiosNet 的 BaseAddress 一致，请求模型可写绝对 URL 覆盖）。</param>
    /// <param name="configureDefaults">实例默认配置（超时、头、序列化器等），null 表示全默认。</param>
    /// <param name="options">底层客户端的实例级选项（代理 / 凭据 / Cookie / 重定向次数 / 自建池 / 并发容量）：构造期固化，写不进 <paramref name="configureDefaults"/>。</param>
    public ApiClient(string baseAddress, Action<AxiosConfig>? configureDefaults = null, AxiosClientOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new ArgumentException("baseAddress 不能为空白。", nameof(baseAddress));
        }

        var defaults = new AxiosConfig { BaseAddress = baseAddress };
        configureDefaults?.Invoke(defaults);
        Axios = new AxiosClient(defaults, options);
        _ownsAxiosClient = true;
        AxiosClientDeclarativeExtensions.Register(Axios, this);
    }

    /// <summary>包装既有 AxiosClient（拦截器 / 连接池 / 实例默认值已就绪的场景）。</summary>
    /// <param name="axiosClient">底层客户端。</param>
    /// <param name="ownsAxiosClient">释放本门面时是否一并释放 <paramref name="axiosClient"/>。</param>
    public ApiClient(AxiosClient axiosClient, bool ownsAxiosClient = false)
        : this(axiosClient, ownsAxiosClient, register: true)
    {
    }

    /// <summary>构造门面；<paramref name="register"/> 为 <c>false</c> 时只创建不登记。</summary>
    internal ApiClient(AxiosClient axiosClient, bool ownsAxiosClient, bool register)
    {
        ArgumentNullException.ThrowIfNull(axiosClient);
        Axios = axiosClient;
        _ownsAxiosClient = ownsAxiosClient;
        if (register)
        {
            AxiosClientDeclarativeExtensions.Register(Axios, this);
        }
    }

    /// <summary>发送请求模型描述的请求，只交付正文：正文类型就是 <see cref="IApiRequest{TResponse}"/> 的泛型，正文为空时返回 <c>default</c>。</summary>
    /// <param name="request">请求模型实例（路径 / 查询 / 头 / 正文的取值来源）。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <exception cref="AxiosException">请求模型定义非法（缺路由特性、占位符未绑定、多个请求体等），错误码 <c>ERR_BAD_OPTION_VALUE</c>，内部异常在 <c>Cause</c>。</exception>
    /// <exception cref="AxiosAotMetadataException">Native AOT 下缺少该模型的生成元数据（源生成器未启用）。</exception>
    public Task<TResponse> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => RequestDataAsync(request, configure: null, cancellationToken);

    /// <summary>同 <see cref="RequestAsync{TResponse}(IApiRequest{TResponse}, CancellationToken)"/>，另支持按请求微调配置。</summary>
    /// <param name="request">请求模型实例。</param>
    /// <param name="configure">在参数绑定与 <see cref="IApiRequestConfigurable.Configure"/> 之后、授权之前调用。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public Task<TResponse> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
        => RequestDataAsync(request, configure, cancellationToken);

    /// <summary>发送请求模型描述的请求，交付完整响应（状态码 / 响应头 / 请求消息 / 正文）：要读这些元数据时用它。</summary>
    /// <param name="request">请求模型实例（路径 / 查询 / 头 / 正文的取值来源）。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <exception cref="AxiosException">请求模型定义非法（缺路由特性、占位符未绑定、多个请求体等），错误码 <c>ERR_BAD_OPTION_VALUE</c>，内部异常在 <c>Cause</c>。</exception>
    /// <exception cref="AxiosAotMetadataException">Native AOT 下缺少该模型的生成元数据（源生成器未启用）。</exception>
    public Task<AxiosResponse<TResponse>> RequestFullAsync<TResponse>(
        IApiRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => RequestResponseAsync(request, configure: null, cancellationToken);

    /// <summary>同 <see cref="RequestFullAsync{TResponse}(IApiRequest{TResponse}, CancellationToken)"/>，另支持按请求微调配置。</summary>
    /// <param name="request">请求模型实例。</param>
    /// <param name="configure">在参数绑定与 <see cref="IApiRequestConfigurable.Configure"/> 之后、授权之前调用。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public Task<AxiosResponse<TResponse>> RequestFullAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
        => RequestResponseAsync(request, configure, cancellationToken);

    /// <summary>绑定 + 授权 + 发送，交付完整响应；装配期异常按异步方法的时序交给返回的 Task。</summary>
    private Task<AxiosResponse<TResponse>> RequestResponseAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);

            var prepared = Prepare(request, configure, cancellationToken);
            return SendAsync<TResponse>(request, prepared.Config, prepared.Auth, cancellationToken, request.GetType().Name);
        }
        catch (Exception ex)
        {
            return Task.FromException<AxiosResponse<TResponse>>(ex);
        }
    }

    /// <summary>发出请求模型描述的请求，只交付响应正文（<c>Data</c>）；这是唯一的一层异步等待，不再转调完整响应版本。</summary>
    private async Task<TResponse> RequestDataAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = Prepare(request, configure, cancellationToken);
        var response = await SendAsync<TResponse>(request, prepared.Config, prepared.Auth, cancellationToken, request.GetType().Name)
            .ConfigureAwait(false);
        return response.Data!;
    }

    /// <summary>请求模型的绑定 + 授权结果：配置与本次的托管凭据适配器（没有托管凭据时为 <c>null</c>）。</summary>
    private readonly struct PreparedRequest
    {
        public PreparedRequest(AxiosConfig config, ApiCredentialProviderAdapter? auth)
        {
            Config = config;
            Auth = auth;
        }

        /// <summary>本次请求的生效配置。</summary>
        public AxiosConfig Config { get; }

        /// <summary>本次的托管凭据适配器；<c>null</c> 表示没走托管凭据。</summary>
        public ApiCredentialProviderAdapter? Auth { get; }
    }

    /// <summary>请求模型的装配：取元数据 → 绑定配置 → 授权。失败（含声明错误）同步抛出，由调用方决定交给 Task 还是直接冒泡。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    private PreparedRequest Prepare(
        object request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        var metadata = AcquireMetadata(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);
        return new PreparedRequest(config, ApplyAuthorization(metadata.Authorization, request, config));
    }

    /// <summary>按请求模型拼出最终 URL（路径替换、查询参数与 BaseAddress 合并），不发请求；返回实际发送形态（已转义）。</summary>
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
        var metadata = AcquireMetadata(request.GetType());
        var config = BuildConfig(metadata, request, configure: null, cancellationToken: default);
        return BindingErrors.Run(
            config,
            request.GetType().Name,
            (client: Axios, config),
            static state => state.client.GetUri(state.config));
    }

    /// <summary>取请求模型的绑定元数据，并把「模型声明非法」改写成 <see cref="AxiosException"/>。</summary>
    /// <exception cref="AxiosException">模型声明非法（<c>ERR_BAD_OPTION_VALUE</c>），原始异常在 <c>Cause</c>；<see cref="AxiosAotMetadataException"/> 原样透传。</exception>
    private static RequestModelMetadata AcquireMetadata(
        [DynamicallyAccessedMembers(RequestModelMetadata.ModelMembers)] Type requestType)
    {
        try
        {
            return RequestModelMetadata.Get(requestType);
        }
        catch (AxiosAotMetadataException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw AxiosException.InvalidOption(ex.Message, config: null, inner: ex);
        }
    }

    /// <summary>按请求模型的声明拼出这次请求的配置：路由、静态头、绑定参数与授权都在这条路径上落地。</summary>
    private AxiosConfig BuildConfig(
        RequestModelMetadata metadata,
        object request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken)
    {
        // 格式化钩子只有实例默认值一个来源，绑定参数时就要用。
        var formatter = Axios.Defaults.Serialization.ValueText;
        var target = request.GetType().Name;
        var config = Axios.NewEffectiveConfig();
        config.Method = metadata.Method;

        // 声明式装配失败统一归 ERR_BAD_REQUEST（路径 / 查询 / 头 / 正文同口径）。
        config.Url = BindingErrors.Run(
            config,
            target,
            (metadata, request, formatter),
            static state => state.metadata.ResolveUrl(state.request, state.formatter));

        // 无查询参数时不写：显式 null 表示摘掉实例默认值。
        if (BindingErrors.Run(
                config,
                target,
                (metadata, request, formatter),
                static state => state.metadata.ResolveQuery(state.request, state.formatter)) is { } query)
        {
            config.Params = query;
        }

        if (BindingErrors.Run(
                config,
                target,
                (metadata, request, formatter),
                static state => state.metadata.ResolveBody(state.request, state.formatter)) is { } body)
        {
            config.Body = body;
        }

        // 类级静态头先写，属性上的 [Header] / [HeaderCollection] 覆盖同名项。
        BindingErrors.Run(
            config,
            target,
            (metadata, config),
            static state => state.metadata.ApplyStaticHeaders(state.config));

        BindingErrors.Run(
            config,
            target,
            (metadata, request, config, formatter),
            static state => state.metadata.ApplyHeaders(state.request, state.config, state.formatter));

        // 类 / 属性级编码声明是默认值，可被 Configure 与调用方 configure 覆盖。
        if (metadata.ContentType is { } declaredContentType)
        {
            config.ContentType = declaredContentType;
        }

        if (metadata.QueryCollectionFormat is { } declaredCollectionFormat)
        {
            config.Serialization.CollectionFormat = declaredCollectionFormat;
        }

        // 仅在调用方传入可取消的令牌时才写入：None 会覆盖实例默认令牌。
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        (request as IApiRequestConfigurable)?.Configure(config);
        configure?.Invoke(config);

        // 绑定期后再改 ValueText 会让 URL / 头 / 正文分属两个钩子，直接报错。
        if (!ReferenceEquals(config.Serialization.ValueText, formatter))
        {
            throw AxiosException.InvalidOption(
                $"请求模型 {target} 在绑定期之后修改了 Serialization.ValueText：路径 / 查询 / 请求头 / 正文已按原钩子定型，改了不会重算。"
                + "请把钩子设在客户端实例默认值上（AxiosClient.Defaults.Serialization.ValueText）；"
                + "直接调用（GetAsync / PostAsync 等）的按请求配置不受此限制。",
                config);
        }

        // [Multipart] 模型：Content-Type 由库设定；调用方或 Configure 显式设置过则保留。
        if (metadata.IsMultipart && !config.ContentTypeSet)
        {
            config.ContentType = AxiosContentType.MultipartFormData;
        }

        // 顺序与快捷方法一致：先写完头，再执行删头。
        AxiosClient.ApplyRemoveHeaders(config);

        // 本次请求的装配失败改判器：内核在请求成型阶段抛错时用它换成 ERR_BAD_REQUEST。
        config.ConstructionFailureMap = metadata.FailureMapper;
        return config;
    }

    /// <summary>授权链路：<see cref="AllowAnonymousAttribute"/> 清空凭据来源；返回的适配器 <c>null</c> 表示本次没走托管凭据。</summary>
    /// <param name="authorization">声明上的授权策略。</param>
    /// <param name="request">本次调用上下文；没有观测者读实参的入口可以给 <c>null</c>，此时必不走托管凭据。</param>
    /// <param name="config">本次请求配置。</param>
    internal ApiCredentialProviderAdapter? ApplyAuthorization(
        ApiAuthorization authorization,
        object? request,
        AxiosConfig config)
    {
        if (authorization.Mode == ApiAuthorizationMode.Anonymous)
        {
            // 匿名：清空凭据来源；实例默认的静态槽位用显式 null 压住，按请求显式设置的 Transport.Auth 保持不动。
            if (config.TransportOrNull?.AuthSet != true && Axios.Defaults.TransportOrNull?.Auth is not null)
            {
                config.Transport.Auth = null;
            }

            config.InternalCredentialProvider = null;
            return null;
        }

        // 只看生效的静态槽位：按请求显式设置（含显式清空）压过实例默认值。
        var effectiveAuth = config.TransportOrNull is { AuthSet: true } transport
            ? transport.Auth
            : Axios.Defaults.TransportOrNull?.Auth;

        // 声明里写了 scheme 时，静态槽位的方案必须一致。
        if (authorization.Mode == ApiAuthorizationMode.Required
            && effectiveAuth?.Scheme is { } slotScheme
            && !string.Equals(slotScheme, authorization.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw AxiosException.InvalidOption(
                $"授权声明要求 scheme“{authorization.Scheme}”，但 Transport.Auth 的静态凭据是“{slotScheme}”。"
                + "两者必须一致：改静态凭据的 scheme（如 AxiosCredential.Bearer(token, \""
                + authorization.Scheme
                + "\")），或去掉声明里的 scheme。",
                config);
        }

        if (CredentialProvider is null)
        {
            return null;
        }

        if (effectiveAuth is not null)
        {
            throw AxiosException.InvalidOption(
                "Transport.Auth 与 CredentialProvider 同时设置：两者都是凭据来源，且只占一个槽位。"
                + "保留其中一个：静态 / 提供方凭据请在 Transport.Auth 上配置，或去掉它改用 CredentialProvider。",
                config);
        }

        // 走到这里说明本次走托管凭据，调用方必然带了调用载荷（见 RequiresInvocationPayload）。
        var adapter = new ApiCredentialProviderAdapter(CredentialProvider, request!, authorization.Scheme, _credentialCache);
        config.InternalCredentialProvider = adapter;
        return adapter;
    }

    /// <summary>本次调用是否有观察者会读实参（凭据提供方 / 错误工厂）：有才需要组装调用对象与实参数组。</summary>
    internal bool RequiresInvocationPayload => CredentialProvider is not null || ErrorFactory is not null;

    /// <summary>发送请求；「服务端给了响应却是错误状态码」时交给 <see cref="ErrorFactory"/>；<paramref name="auth"/> 为 <c>null</c> 表示本次没有托管凭据。</summary>
    /// <remarks>
    /// 没有托管凭据也没有错误工厂时直接走强类型内核路径：不建恢复状态机，装配失败改判由配置上的钩子完成。
    /// </remarks>
    internal Task<AxiosResponse<TResponse>> SendAsync<TResponse>(
        object? request,
        AxiosConfig config,
        ApiCredentialProviderAdapter? auth,
        CancellationToken cancellationToken,
        string targetName)
        => auth is null && ErrorFactory is null
            ? SendPreparedDeclarativeAsync<TResponse>(config)
            : SendCoreAsync(request, config, auth, cancellationToken, TypedSender<TResponse>.Send, targetName);

    /// <summary>发送请求并交付非泛型响应（正文按 <see cref="System.Text.Json.JsonElement"/>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    internal Task<AxiosResponse> SendUntypedAsync(
        object? request,
        AxiosConfig config,
        ApiCredentialProviderAdapter? auth,
        CancellationToken cancellationToken,
        string targetName)
        => auth is null && ErrorFactory is null
            ? SendPreparedDeclarativeAsync(config)
            : SendCoreAsync(request, config, auth, cancellationToken, ResponseSenders.Untyped, targetName);

    /// <summary>只完成请求、不装配正文（接口声明返回 <c>Task</c>）；错误处理同 <see cref="SendAsync{TResponse}"/>。</summary>
    internal Task SendWithoutBodyAsync(
        object? request,
        AxiosConfig config,
        ApiCredentialProviderAdapter? auth,
        CancellationToken cancellationToken,
        string targetName)
        => auth is null && ErrorFactory is null
            ? SendPreparedDeclarativeVoidAsync(config)
            : SendWithoutBodyCoreAsync(request, config, auth, cancellationToken, targetName);

    /// <summary>慢路：需要 401 重放或错误工厂接管时用，正文不装配、只完成请求。</summary>
    private async Task SendWithoutBodyCoreAsync(
        object? request,
        AxiosConfig config,
        ApiCredentialProviderAdapter? auth,
        CancellationToken cancellationToken,
        string targetName)
    {
        var response = await SendCoreAsync(request, config, auth, cancellationToken, ResponseSenders.Void, targetName)
            .ConfigureAwait(false);

        DisposeDiscardedBody(response);
    }

    /// <summary>声明式快路：配置已填好、没有托管凭据也没有错误工厂，直接交给强类型内核路径（不再套恢复层）。</summary>
    internal Task<AxiosResponse<TResponse>> SendPreparedDeclarativeAsync<TResponse>(AxiosConfig config)
        => Axios.SendPreparedTypedAsync<TResponse>(config);

    /// <summary>声明式快路的非泛型版本（正文按 <see cref="System.Text.Json.JsonElement"/> 原样给）。</summary>
    internal Task<AxiosResponse> SendPreparedDeclarativeAsync(AxiosConfig config)
        => Axios.SendPreparedAsync(config);

    /// <summary>声明式快路的非泛型版本：只完成请求，正文丢弃。</summary>
    internal async Task SendPreparedDeclarativeVoidAsync(AxiosConfig config)
    {
        var response = await Axios.SendPreparedAsync(config, typeof(void)).ConfigureAwait(false);
        DisposeDiscardedBody(response);
    }

    /// <summary>丢弃正文时的释放：正文归调用方（<c>responseType</c> 配成 Stream）才释放；短路响应的 Data 归调用方，不释放。</summary>
    private static void DisposeDiscardedBody(AxiosResponse response)
    {
        if (response.Request is not null)
        {
            (response.Data as IDisposable)?.Dispose();
        }
    }

    /// <summary>按返回类型缓存的发送委托。</summary>
    private static class TypedSender<T>
    {
        /// <summary>泛型发送委托（按 <typeparamref name="T"/> 缓存）。</summary>
        internal static readonly Func<AxiosClient, AxiosConfig, Task<AxiosResponse<T>>> Send =
            static (client, config) => client.SendPreparedTypedAsync<T>(config);
    }

    /// <summary>非泛型响应的发送委托：<c>null</c> = 内核默认形态，<c>typeof(void)</c> = 丢弃正文。</summary>
    private static class ResponseSenders
    {
        /// <summary>内核默认形态（正文按 <see cref="System.Text.Json.JsonElement"/>）的发送委托。</summary>
        internal static readonly Func<AxiosClient, AxiosConfig, Task<AxiosResponse>> Untyped =
            static (client, config) => client.SendPreparedAsync(config);

        /// <summary>只完成请求、丢弃正文（<c>typeof(void)</c>）的发送委托。</summary>
        internal static readonly Func<AxiosClient, AxiosConfig, Task<AxiosResponse>> Void =
            static (client, config) => client.SendPreparedAsync(config, typeof(void));
    }

    /// <summary>慢路发送：需要 401 重放或错误工厂接管时用，一次发送在同一条链路里收尾；<paramref name="targetName"/> 用于装配失败消息。</summary>
    private async Task<TResult> SendCoreAsync<TResult>(
        object? request,
        AxiosConfig config,
        ApiCredentialProviderAdapter? auth,
        CancellationToken cancellationToken,
        Func<AxiosClient, AxiosConfig, Task<TResult>> send,
        string targetName)
    {
        try
        {
            var bodyReplayable = CanPrepareReplay(config, auth);
            var bodyIsContent = config.Body is HttpContent;

            if (bodyReplayable && bodyIsContent)
            {
                // 不预读正文：管线在 401 收尾保留正文，只在真要重放时读。
                config.RetainBodyContent = true;
            }

            HttpContent? retained = null;

            try
            {
                return await send(Axios, config).ConfigureAwait(false);
            }
            catch (AxiosException error) when (CanReplayForAuth(error, auth, bodyReplayable))
            {
                // 401 收尾没有释放正文，快照在这里读；取失败配置里的那份（可能经过 transformRequest）。
                retained = TakeRetainedBody(error);

                // 重放是同一个逻辑请求：沿用上次实际发出的幂等键。
                if (error.Config?.IdempotencyOrNull is { Enabled: true, GeneratedKey: { } key })
                {
                    config.Resilience.Idempotency.Key = key;
                }

                if (!await ReauthorizeAsync(auth!, config, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                if (bodyIsContent)
                {
                    RequestBodySnapshot? snapshot = null;
                    if (retained is not null)
                    {
                        try
                        {
                            snapshot = await RequestBodySnapshot
                                .TryCreateAsync(retained, ResolveMaxRequestBytes(config), cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 快照读不出来就不重放，原样抛出本次 401。
                            snapshot = null;
                        }
                    }

                    // 读不出来或超 MaxRequestBytes（未知长度正文不提前拦截）：本次退成不可重放。
                    if (snapshot is null)
                    {
                        throw;
                    }

                    // 重放前换成新的内容实例：管线的保留缓冲不复用。
                    config.Body = snapshot.CreateContent();
                }

                // 第二次发送自己会释放新正文，不要再保留。
                config.RetainBodyContent = false;

                return await send(Axios, config).ConfigureAwait(false);
            }
            catch (AxiosException error)
            {
                // 本次 401 不重放：取走管线保留的正文，交给 finally 释放。
                retained = TakeRetainedBody(error);
                throw;
            }
            finally
            {
                config.RetainBodyContent = false;
                retained?.Dispose();
            }
        }
        catch (AxiosException error) when (error.IsRequestConstructionFailure)
        {
            // 查询展平阶段的取值 / 格式化失败：改判 ERR_BAD_REQUEST，原因保留原始异常。
            throw RequestBindingErrors.Create(
                error.Config,
                request: null,
                targetName,
                error.Cause ?? error);
        }
        catch (AxiosException error) when (ErrorFactory is not null && error.Response is not null)
        {
            // 只接管「服务端给了响应」的错误；工厂返回 null 时原样再抛。
            var replacement = await ErrorFactory
                .CreateAsync(new ApiErrorContext(request!, error), cancellationToken)
                .ConfigureAwait(false);

            if (replacement is not null)
            {
                throw replacement;
            }

            throw;
        }
    }

    /// <summary>首次发送前判定 401 重放是否值得准备（四项前提同时成立）；只做判定，不读正文。</summary>
    private bool CanPrepareReplay(AxiosConfig config, ApiCredentialProviderAdapter? auth)
    {
        if (!RetryOnUnauthorized
            || auth is null
            || !auth.CanRefresh
            || !(config.AllowUnsafeRetry || Axios.Defaults.AllowUnsafeRetry || RetryPolicy.IsIdempotent(config.Method)))
        {
            return false;
        }

        return BodyReplayability.Classify(config) switch
        {
            // 数据型正文每次发送都由模型重新装配。
            BodyReplayKind.Rebuild => true,

            // 白名单内的内存内容可跨第二次发送重新序列化。
            BodyReplayKind.Content when config.Body is HttpContent => true,

            // None：分类器已判不可重放；Streamed：流随第一次请求释放，跨调用无法安全重放。
            _ => false,
        };
    }

    /// <summary>取走发送管线在 401 收尾保留的正文，并清掉失败配置上的引用。</summary>
    private static HttpContent? TakeRetainedBody(AxiosException error)
    {
        var config = error.Config;
        if (config is null)
        {
            return null;
        }

        var content = config.RetainedReplayContent;
        config.RetainedReplayContent = null;
        return content;
    }

    /// <summary>本次请求生效的正文上限；实例默认未设置时为 <c>null</c>（不限制）。</summary>
    private long? ResolveMaxRequestBytes(AxiosConfig config)
        => config.LimitsOrNull is { MaxRequestBytesSet: true } limits
            ? limits.MaxRequestBytes
            : Axios.Defaults.LimitsOrNull?.MaxRequestBytes;

    /// <summary>这次 401 值不值得重放：首次发送前的判定说正文可重放，且本次凭据由库托管。</summary>
    private static bool CanReplayForAuth(AxiosException error, ApiCredentialProviderAdapter? auth, bool bodyReplayable)
        => bodyReplayable
            && auth is not null
            && auth.IsManaged
            && error.Status == 401;

    /// <summary>401 后作废旧凭据并重取一次；返回 <c>false</c> 表示没拿到新值（重放没有意义）。</summary>
    private static async ValueTask<bool> ReauthorizeAsync(
        ApiCredentialProviderAdapter auth,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        // 旧值由适配器记录，与落点无关；完整 Authorization 头值按无条件作废。
        var previousValue = auth.LastValue;
        auth.Invalidate(auth.LastValueIsCompleteAuthorization ? null : previousValue);

        // 重取只取值；落点留到重放那次发送时写进合并后的配置。
        var next = await auth.RefreshAsync(config, cancellationToken).ConfigureAwait(false);
        return next is { Value: { } value }
            && !string.Equals(value, previousValue, StringComparison.Ordinal);
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
