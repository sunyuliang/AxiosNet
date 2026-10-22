using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

/// <summary>
/// 声明式 API 客户端：请求模型（<see cref="IApiRequest{TResponse}"/> + 路由/参数特性）描述「怎么访问」，
/// 授权（<see cref="Authorization"/>）描述「凭什么访问」，调用处只剩一句：
/// <code>
/// var client = new ApiClient("https://api.example.com")
/// {
///     Authorization = new BearerAuthorizationProvider(token);
/// };
///
/// var response = await client.RequestAsync(new GetUserRequest { Id = 5 });
/// response.Data.Name; // 返回类型由请求模型自动确定
/// </code>
/// <para>
/// 本类是 <see cref="AxiosClient"/> 的薄门面：请求模型绑定成 AxiosConfig 后走 AxiosNet 的完整管线
/// （连接池、拦截器、超时、重试语义、错误模型 <see cref="AxiosException"/> 全部保留）。
/// 深度定制直接取 <see cref="Axios"/>。
/// </para>
/// </summary>
public sealed partial class ApiClient : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsAxiosClient;

    /// <summary>底层 AxiosNet 客户端（实例默认值、拦截器、连接池都从它进入）。</summary>
    public AxiosClient Axios { get; }

    /// <summary>
    /// 授权提供方：每次请求发送前调用（在模型绑定与 <see cref="IApiRequestConfigurable.Configure"/> 之后），
    /// 把凭据写进最终配置。null 表示不授权（匿名访问）。
    /// </summary>
    public IAuthorizationProvider? Authorization { get; set; }

    /// <summary>
    /// URL 参数格式化钩子：绑定 <c>[Path]</c> 与 <c>[Query]</c> 的值时调用，返回的字符串直接作为该参数的文本
    /// （路径段仍会被 URL 转义）；返回 <c>null</c> 表示「本次不处理」，回落到默认规则。
    /// <para>
    /// 只能在发送前设置（绑定发生在请求构造期）：用于枚举自定义写法、日期格式、单位换算等。
    /// 数组 / 字典值整体传入；返回字符串即整体替换（不再走 <c>a[]=v</c> 规则）。
    /// </para>
    /// </summary>
    public Func<object, string?>? UrlParameterFormatter { get; set; }

    /// <summary>
    /// HTTP 状态码错误的异常工厂：非 null 时，状态码不合法（<see cref="AxiosException.Response"/> 非 null）
    /// 的请求会先交给它构造异常；返回 <c>null</c> 表示仍抛原始 <see cref="AxiosException"/>。
    /// <para>
    /// 只处理「服务端返回了响应」的错误；网络错误、超时、取消、配置错误不会被接管。
    /// 工厂自身抛出的异常原样冒泡（与拦截器一致）。
    /// </para>
    /// <para>内置实现见 <see cref="ApiErrorFactory{TError}"/>（把错误体反序列化成强类型）。</para>
    /// </summary>
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
    public async Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        // 授权最后做：凭据反映的是绑定与 Configure 之后的最终形态（如按最终 URL 签名才不会漏参数）
        if (Authorization is not null)
        {
            await Authorization
                .ApplyAsync(new ApiAuthContext(request, config), cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            return await Axios.RequestAsync<TResponse>(config).ConfigureAwait(false);
        }
        catch (AxiosException error) when (ErrorFactory is not null && error.Response is not null)
        {
            // 只接管「服务端给了响应」的错误：网络 / 超时 / 取消 / 配置错误保持原样，
            // 调用方的 catch (AxiosException) 仍然成立（工厂返回 null 时原样再抛）。
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

    /// <summary>
    /// 按请求模型拼出最终 URL（含路径替换、查询参数与 BaseAddress 合并），不发请求。
    /// 可用于日志与签名；返回实际发送形态（已转义）。
    /// </summary>
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
            Url = metadata.ResolveUrl(request, UrlParameterFormatter),
            Params = metadata.ResolveQuery(request, UrlParameterFormatter)
        };

        var body = metadata.ResolveBody(request);
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

        return config;
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
