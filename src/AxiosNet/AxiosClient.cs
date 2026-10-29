using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// axios 风格的 HTTP 客户端实例（对应 JS 的 axios instance）：支持 baseURL / method / headers / params /
/// data / timeout / auth / responseType / validateStatus / 取消令牌 / 拦截器，默认走连接池复用连接。
/// </summary>
public sealed partial class AxiosClient : IDisposable, IAsyncDisposable
{
    private readonly HttpConnectionPool? _pool;
    private readonly HttpClient? _customClient;
    private readonly HttpMessageHandler? _customHandler;
    private readonly bool _ownsPool;
    private readonly bool _ownsCustomHandler;
    private int _disposed;
    private int _inFlight;
    private int _resourcesDisposed;

    /// <summary>
    /// 本实例的尝试管线：构造期编译成单个委托并固化，此后不可变。
    /// </summary>
    private readonly AxiosAttemptHandler _attemptPipeline;

    /// <summary>在途并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）：首个受限请求时按当时上限创建并固定，之后改上限会抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    private ConcurrencyGate? _gate;

    /// <summary>
    /// 对应 axios instance.defaults。连接级选项（WithCredentials / MaxRedirects / Proxy / Credentials / PoolOptions）
    /// 构造时已固化，改后不生效；MaxConcurrentRequests 的闸门在首个受限请求时创建，此后改会抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </summary>
    public AxiosConfig Defaults { get; }

    /// <summary>对应 axios instance.interceptors。</summary>
    public AxiosInterceptors Interceptors { get; } = new();

    /// <summary>
    /// 本实例的尝试管线（构造期固化，之后不可变）；未显式传入时是 <see cref="AxiosAttemptPipeline.Default"/>。
    /// </summary>
    public AxiosAttemptPipeline AttemptPipeline { get; }

    /// <summary>本实例的连接池；注入自定义 <see cref="HttpMessageHandler"/> 时为 <c>null</c>。</summary>
    public HttpConnectionPool? Pool => _pool;

    /// <summary>是否使用连接池（即未直接注入 Handler）。</summary>
    public bool UsesConnectionPool => _pool is not null;

    /// <summary>创建实例（未注入 <paramref name="handler"/> 时走连接池）。</summary>
    /// <param name="defaults">实例默认配置（对应 axios instance.defaults）。</param>
    /// <param name="pool">
    /// 共享连接池；与 <paramref name="poolOptions"/> / <paramref name="handler"/> 互斥（同时传入抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </param>
    /// <param name="poolOptions">本实例自建连接池的选项。</param>
    /// <param name="handler">
    /// 直接注入的 Handler；注入后不创建连接池，连接级选项由它自己决定。
    /// </param>
    /// <param name="disposeHandler">释放本实例时是否一并释放注入的 <paramref name="handler"/>。</param>
    /// <param name="pipeline">
    /// 尝试管线：<c>null</c> 用 <see cref="AxiosAttemptPipeline.Default"/>（重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 发送）；
    /// 构造期编译并固化，按请求定制请用拦截器。
    /// </param>
    public AxiosClient(
        AxiosConfig? defaults = null,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true,
        AxiosAttemptPipeline? pipeline = null)
    {
        Defaults = defaults ?? new AxiosConfig();

        // 下面的分支有提前 return，管线先在此初始化。
        AttemptPipeline = pipeline ?? AxiosAttemptPipeline.Default;
        _attemptPipeline = AttemptPipeline.Compile(SendTerminalAsync, Defaults);

        if (handler is not null)
        {
            if (pool is not null)
            {
                throw AxiosException.InvalidOption("不能同时注入 handler 和 pool：注入 handler 时不使用连接池，请二选一。", Defaults);
            }

            if (poolOptions is not null || Defaults.ConnectionOrNull?.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时注入 handler 和连接池选项（poolOptions / defaults.PoolOptions）："
                    + "handler 的连接行为由它自己决定，需要池化请改用 ConnectionPoolOptions.HandlerFactory。", Defaults);
            }

            ValidateConnectionLevelOptions(defaults, Defaults, ConnectionLevelMessages.Handler);
            _customHandler = handler;
            _ownsCustomHandler = disposeHandler;
            _customClient = new HttpClient(handler, disposeHandler: false)
            {
                // 超时由 AxiosClient 按请求统一控制
                Timeout = Timeout.InfiniteTimeSpan
            };
            return;
        }

        if (pool is not null)
        {
            if (poolOptions is not null || Defaults.ConnectionOrNull?.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 pool 和 poolOptions / defaults.PoolOptions："
                    + "共享池已按自己的 ConnectionPoolOptions 创建，请在创建池时配置，或改为只传 poolOptions 让本实例自建池。", Defaults);
            }

            // 共享池的连接行为已固定，defaults 上的同类选项不生效。
            ValidateConnectionLevelOptions(defaults, Defaults, ConnectionLevelMessages.SharedPool);

            _pool = pool;
            _ownsPool = false;
        }
        else
        {
            // 两处都配时直接报错。
            if (poolOptions is not null && Defaults.ConnectionOrNull?.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 defaults.PoolOptions：两者描述的是同一个自建连接池，请只在一处配置。", Defaults);
            }

            var effective = (poolOptions ?? Defaults.ConnectionOrNull?.PoolOptions ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期带配置抛错。
            HttpConnectionPool.ValidateOptions(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向，> 0 = 覆盖池默认上限；按 *Set 判断是否显式配置。
            if (Defaults.MaxRedirectsSet)
            {
                var maxRedirects = Defaults.ConnectionOrNull?.MaxRedirects ?? 5;

                if (maxRedirects < 0)
                {
                    throw AxiosException.InvalidOption("MaxRedirects 不能为负数；不跟随重定向请设为 0。", Defaults);
                }

                if (maxRedirects == 0)
                {
                    effective.AllowAutoRedirect = false;
                }
                else
                {
                    effective.MaxAutomaticRedirections = maxRedirects;
                }
            }

            // 两处都写时直接报错。
            if (poolOptions is not null)
            {
                if (poolOptions.Proxy is not null && Defaults.ConnectionOrNull?.Proxy is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Proxy 和 defaults.Proxy 上配置代理：请只在一处设置。", Defaults);
                }

                if (poolOptions.Credentials is not null && Defaults.ConnectionOrNull?.Credentials is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Credentials 和 defaults.Credentials 上配置凭据：请只在一处设置。", Defaults);
                }
            }

            effective.Proxy ??= Defaults.ConnectionOrNull?.Proxy;
            effective.Credentials ??= Defaults.ConnectionOrNull?.Credentials;
            if (Defaults.ConnectionOrNull?.WithCredentials is true)
            {
                effective.UseCookies = true;
                effective.Credentials ??= CredentialCache.DefaultCredentials;
            }

            _pool = new HttpConnectionPool(effective);
            _ownsPool = true;
        }
    }

    // ---- getUri：只组装 URL，不发请求（对应 axios.getUri） ----

    /// <summary>按本实例生效配置拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求，返回值已转义。传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并。</summary>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 回放 response.Config 时实例级项取自本实例默认值。
        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.SerializationOrNull?.ParamsSerializer, config: merged).AbsoluteUri;
    }

    /// <summary>按 Action 配置拼出最终请求 URL，不发请求；回调在实例默认值的副本上修改（与快捷方法同一模型）。</summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 直接拼生效配置，不走 MergeConfig：回调里显式写的 BaseAddress 需保留。
        var config = NewEffectiveConfig();
        configure(config);
        return UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.ParamsSerializer, config: config).AbsoluteUri;
    }

    // ---- request(config) 入口 ----

    /// <summary>对应 axios(config)，返回非泛型响应（JSON 时 Data 为 JsonElement）。</summary>
    public Task<AxiosResponse> RequestAsync(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(PreparePerCall(config), dataType: null);
    }

    /// <summary>对应 axios(config)，Data 反序列化为 T。</summary>
    public Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendTypedAsync<T>(PreparePerCall(config));
    }

    /// <summary>用 Action 配置请求的非泛型入口。</summary>
    public Task<AxiosResponse> RequestAsync(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 回调在实例默认值的副本上修改，删头 / 清空头集合都生效。
        var config = NewEffectiveConfig();
        configure(config);
        ApplyRemoveHeaders(config);
        return SendAsync(config, null);
    }

    /// <summary>用 Action 配置请求的泛型入口。</summary>
    public Task<AxiosResponse<T>> RequestAsync<T>(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var config = NewEffectiveConfig();
        configure(config);
        ApplyRemoveHeaders(config);
        return SendTypedAsync<T>(config);
    }

    /// <summary>把调用方交进来的配置整理成本次请求的生效配置：普通配置原样返回，已合并的（如回放 Config）在这里直接合并。</summary>
    private AxiosConfig PreparePerCall(AxiosConfig config)
    {
        if (!config.EffectiveMerged)
        {
            return config;
        }

        var merged = MergeConfig(config);
        merged.EffectiveMerged = true;
        return merged;
    }
}
