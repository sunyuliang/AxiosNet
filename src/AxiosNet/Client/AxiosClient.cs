using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;
using AxiosNet.Serialization;

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
    // 0/1 一次性释放标志。
    private int _disposed;
    // 在途请求数：释放时等它归零。
    private int _inFlight;
    // 池 / Handler 的释放标志。
    private int _resourcesDisposed;

    /// <summary>本实例的尝试管线：构造期编译成单个委托并固化，此后不可变。</summary>
    private readonly AxiosAttemptHandler _attemptPipeline;

    /// <summary>在途并发闸门（<see cref="AxiosClientOptions.MaxInFlight"/>）：首个受限请求时按构造期上限创建并固定。</summary>
    private ConcurrencyGate? _gate;

    /// <summary>闸门容量（构造期定稿）。</summary>
    private readonly int? _maxInFlight;

    /// <summary>排队深度上限（构造期定稿）。</summary>
    private readonly int? _maxQueue;

    /// <summary>备用端点的健康记忆：跨请求共享；未配备用端点时不分配。</summary>
    private FailoverHealth? _failoverHealth;

    /// <summary>构造期定稿的重定向上限，只用于把原因写进异常消息（真实行为由连接池 / 注入的 Handler 决定）。</summary>
    private readonly int _maxRedirects;

    /// <summary>对应 axios instance.defaults：按请求项的默认值（合并到每次请求上）。</summary>
    /// <remarks>连接层与闸门相关的实例级选项在 <see cref="Options"/> 上，构造期固化。</remarks>
    public AxiosConfig Defaults { get; }

    /// <summary>本实例的实例级选项快照：连接池与并发闸门的形态都在构造期按它定死。</summary>
    /// <remarks>
    /// 已把池实际生效的出口（代理 / 凭据 / 重定向次数 / Cookie / 池选项）写在里面。
    /// 构造结束时冻结：再赋值抛 <c>ERR_BAD_OPTION_VALUE</c>；要换形态请新建 <see cref="AxiosClient"/>。
    /// </remarks>
    public AxiosClientOptions Options { get; }

    /// <summary>对应 axios instance.interceptors。</summary>
    public AxiosInterceptors Interceptors { get; } = new();

    /// <summary>
    /// 本实例的尝试管线（构造期固化，之后不可变）；未显式传入时是 <see cref="AxiosAttemptPipeline.Default"/>。
    /// </summary>
    public AxiosAttemptPipeline AttemptPipeline { get; }

    /// <summary>本实例的连接池；注入自定义 <see cref="HttpMessageHandler"/> 时为 <c>null</c>。</summary>
    /// <remarks>
    /// 传入的共享池不归本实例所有（释放本实例不关闭它），自建池在有在途请求时把关闭推迟到最后一个请求结束。
    /// 已关闭的池：<see cref="HttpConnectionPool.GetStatistics"/> 报 0 个节点，<c>Lease</c> / <c>Prewarm</c> 抛 <see cref="ObjectDisposedException"/>。
    /// </remarks>
    public HttpConnectionPool? Pool => _pool;

    /// <summary>是否使用连接池（即未直接注入 Handler）。</summary>
    public bool UsesConnectionPool => _pool is not null;

    /// <summary>创建实例（未注入 <paramref name="handler"/> 时走连接池）。</summary>
    /// <param name="defaults">实例默认配置：每次请求合并到它上面。</param>
    /// <param name="options">实例级选项（代理 / 凭据 / Cookie / 重定向次数 / 自建池 / 并发容量）：构造期固化，之后改不生效。</param>
    /// <param name="pool">
    /// 共享连接池；与 <paramref name="poolOptions"/> / <paramref name="options"/> 里的 <see cref="AxiosClientOptions.Pool"/> /
    /// <paramref name="handler"/> 互斥（同时传入抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </param>
    /// <param name="poolOptions">本实例自建连接池的选项。</param>
    /// <param name="handler">直接注入的 Handler；注入后不创建连接池，连接层选项由它自己决定。</param>
    /// <param name="disposeHandler">释放本实例时是否一并释放注入的 <paramref name="handler"/>。</param>
    /// <param name="pipeline">尝试管线：<c>null</c> 用 <see cref="AxiosAttemptPipeline.Default"/>，构造期编译并固化。</param>
    public AxiosClient(
        AxiosConfig? defaults = null,
        AxiosClientOptions? options = null,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true,
        AxiosAttemptPipeline? pipeline = null)
    {
        Defaults = defaults ?? new AxiosConfig();

        // 持自己的一份快照。
        Options = (options ?? new AxiosClientOptions()).Clone();

        // 每个实例一份序列化注册表，配置里自带一份时沿用。
        _ = Defaults.Serialization.BodySerializers;

        // 下面的分支有提前 return，管线先在此初始化。
        AttemptPipeline = pipeline ?? AxiosAttemptPipeline.Default;
        _attemptPipeline = AttemptPipeline.Compile(SendTerminalAsync, Defaults);

        // 闸门容量与 Handler 无关，无条件在构造期校验。
        Options.ValidateConcurrency(Defaults);

        // 容量在构造期定稿。
        _maxInFlight = Options.MaxInFlight;
        _maxQueue = Options.MaxQueue;

        // 重定向文案基准：先取声明值，池分支再用生效值覆盖。
        _maxRedirects = Options.MaxRedirects;

        if (handler is not null)
        {
            if (pool is not null)
            {
                throw AxiosException.InvalidOption("不能同时注入 handler 和 pool：注入 handler 时不使用连接池，请二选一。", Defaults);
            }

            if (poolOptions is not null || Options.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时注入 handler 和连接池选项（poolOptions / AxiosClientOptions.Pool）："
                    + "handler 的连接行为由它自己决定，需要池化请改用 ConnectionPoolOptions.HandlerFactory。", Defaults);
            }

            ValidateConnectionLevelOptions(Options, Defaults, ConnectionLevelMessages.Handler);
            _customHandler = handler;
            _ownsCustomHandler = disposeHandler;
            _customClient = new HttpClient(handler, disposeHandler: false)
            {
                // 超时由 AxiosClient 按请求统一控制
                Timeout = Timeout.InfiniteTimeSpan
            };

            // 该分支提前返回，冻结仍要执行。
            Options.Freeze();
            return;
        }

        if (pool is not null)
        {
            if (poolOptions is not null || Options.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 pool 和 poolOptions / AxiosClientOptions.Pool："
                    + "共享池已按自己的 ConnectionPoolOptions 创建，请在创建池时配置，或改为只传 poolOptions 让本实例自建池。", Defaults);
            }

            // 共享池的连接行为已固定，options 上的同类选项不生效。
            ValidateConnectionLevelOptions(Options, Defaults, ConnectionLevelMessages.SharedPool);

            _pool = pool;
            _ownsPool = false;
            // 池实际生效的出口回写 Options。
            SyncOptionsFromPool();

            // 文案基准取池生效值。
            _maxRedirects = Options.MaxRedirects;
        }
        else
        {
            // 两处都配时直接报错。
            if (poolOptions is not null && Options.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 AxiosClientOptions.Pool：两者描述的是同一个自建连接池，请只在一处配置。", Defaults);
            }

            var effective = (poolOptions ?? Options.Pool ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期抛错。
            ConnectionPoolOptionsValidator.Validate(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向，> 0 = 覆盖池默认上限；按 *Set 判断是否显式配置。
            if (Options.MaxRedirectsSet)
            {
                var maxRedirects = Options.MaxRedirects;

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
                if (poolOptions.Proxy is not null && Options.Proxy is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Proxy 和 AxiosClientOptions.Proxy 上配置代理：请只在一处设置。", Defaults);
                }

                if (poolOptions.Credentials is not null && Options.Credentials is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Credentials 和 AxiosClientOptions.Credentials 上配置凭据：请只在一处设置。", Defaults);
                }
            }

            effective.Proxy ??= Options.Proxy;
            effective.Credentials ??= Options.Credentials;
            if (Options.WithCredentials)
            {
                effective.UseCookies = true;
                effective.Credentials ??= CredentialCache.DefaultCredentials;
            }

            _pool = new HttpConnectionPool(effective);
            _ownsPool = true;
            // 回写 Options，使各 getter 与实际一致。
            SyncOptionsFromPool();

            _maxRedirects = Options.MaxRedirects;
        }

        // 构造收尾：选项固化，此后改 Options 报错。
        Options.Freeze();
    }

    /// <summary>创建<b>自建连接池</b>的实例：最常用形态，池归本实例所有并在释放时关闭。</summary>
    /// <param name="defaults">实例默认配置：每次请求合并到它上面。</param>
    /// <param name="poolOptions">自建池的选项；<c>null</c> 用默认值。</param>
    /// <param name="options">实例级选项；<c>null</c> 用默认值。</param>
    /// <param name="pipeline">尝试管线；<c>null</c> 用 <see cref="AxiosAttemptPipeline.Default"/>。</param>
    public static AxiosClient Pooled(
        AxiosConfig? defaults = null,
        ConnectionPoolOptions? poolOptions = null,
        AxiosClientOptions? options = null,
        AxiosAttemptPipeline? pipeline = null)
        => new(defaults, options, null, poolOptions, null, true, pipeline);

    /// <summary>创建<b>直接注入 Handler</b> 的实例：不使用连接池，连接行为由 <paramref name="handler"/> 自己决定。</summary>
    /// <param name="handler">注入的 Handler，不能为 <c>null</c>。</param>
    /// <param name="defaults">实例默认配置。</param>
    /// <param name="options">实例级选项（连接层选项在注入 Handler 时不生效）。</param>
    /// <param name="disposeHandler">释放本实例时是否一并释放 <paramref name="handler"/>。</param>
    /// <param name="pipeline">尝试管线；<c>null</c> 用默认管线。</param>
    public static AxiosClient WithHandler(
        HttpMessageHandler handler,
        AxiosConfig? defaults = null,
        AxiosClientOptions? options = null,
        bool disposeHandler = true,
        AxiosAttemptPipeline? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new(defaults, options, null, null, handler, disposeHandler, pipeline);
    }

    /// <summary>创建<b>共享已有连接池</b>的实例：不拥有该池，释放本实例不会关闭它。</summary>
    /// <param name="pool">共享池，不能为 <c>null</c>。</param>
    /// <param name="defaults">实例默认配置。</param>
    /// <param name="options">实例级选项（连接层选项以池为准）。</param>
    /// <param name="pipeline">尝试管线；<c>null</c> 用默认管线。</param>
    public static AxiosClient WithSharedPool(
        HttpConnectionPool pool,
        AxiosConfig? defaults = null,
        AxiosClientOptions? options = null,
        AxiosAttemptPipeline? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new(defaults, options, pool, null, null, true, pipeline);
    }

    /// <summary>连接层配置在构造期定型：把池实际生效的出口写回 <see cref="Options"/>，使 getter 反映真实行为。</summary>
    private void SyncOptionsFromPool()
    {
        if (_pool is not null)
        {
            Options.AbsorbPoolEffective(_pool.EffectiveOptions);
        }
    }

    // getUri：只组装 URL，不发请求（对应 axios.getUri）

    /// <summary>按本实例生效配置拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求，返回值已转义。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：实例级项取自本实例，写在该对象上的值无效。</remarks>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.SerializationOrNull?.QueryEncoder, config: merged).AbsoluteUri;
    }

    /// <summary>按 Action 配置拼出最终请求 URL，不发请求；回调在实例默认值的副本上修改（与快捷方法同一模型）。</summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 回调里显式写的 BaseAddress 需保留。
        var config = NewEffectiveConfig();
        configure(config);
        return UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.QueryEncoder, config: config).AbsoluteUri;
    }

    // request(config) 入口

    /// <summary>对应 axios(config)，返回非泛型响应（JSON 时 Data 为 JsonElement）。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：实例级项取自本实例，写在该对象上的值无效。</remarks>
    public Task<AxiosResponse> RequestAsync(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(PreparePerCall(config), dataType: null);
    }

    /// <summary>对应 axios(config)，Data 反序列化为 T。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：实例级项取自本实例，写在该对象上的值无效。</remarks>
    public Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendTypedAsync<T>(PreparePerCall(config));
    }

    /// <summary>用 Action 配置请求的非泛型入口。</summary>
    public Task<AxiosResponse> RequestAsync(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 回调在实例默认值的副本上修改。
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

    /// <summary>
    /// 按目标类型发送：<paramref name="dataType"/> 为 <c>null</c> 时按内核默认形态交付（JSON 正文为 JsonElement），
    /// 为 <c>typeof(void)</c> 时只完成请求、不装配正文，其余类型按泛型入口同款规则反序列化。
    /// </summary>
    internal Task<AxiosResponse> SendInternalAsync(AxiosConfig config, Type? dataType, AxiosResponse? target)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(PreparePerCall(config), dataType, target);
    }

    /// <summary>把调用方交进来的配置整理成本次请求的生效配置。</summary>
    /// <remarks>
    /// 只处理库产出的已合并配置（<see cref="AxiosConfig.EffectiveMerged"/> 为 <c>true</c>，如回放 <c>response.Config</c>），
    /// 把它重新落到本实例默认值上；普通配置（<c>EffectiveMerged</c> 为 <c>false</c>）原样返回，留给管线阶段 1 合并。
    /// </remarks>
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

    /// <summary>发送一份库内已填好本次请求项的配置（声明式门面用），不再经过 <see cref="PreparePerCall"/> 的二次合并。</summary>
    /// <param name="config">已合并的按请求配置。</param>
    internal Task<AxiosResponse> SendPreparedAsync(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(config, dataType: null, target: null);
    }

    /// <summary>同 <see cref="SendPreparedAsync(AxiosConfig)"/>，另按目标类型反序列化。</summary>
    /// <typeparam name="T">正文的目标类型。</typeparam>
    /// <param name="config">已合并的按请求配置。</param>
    internal Task<AxiosResponse<T>> SendPreparedTypedAsync<T>(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendTypedAsync<T>(config);
    }

    /// <summary>同 <see cref="SendPreparedAsync(AxiosConfig)"/>，只完成请求、不装配正文（<paramref name="dataType"/> 为 <c>typeof(void)</c>）。</summary>
    /// <param name="config">已合并的按请求配置。</param>
    /// <param name="dataType">目标类型；<c>typeof(void)</c> 表示丢弃正文。</param>
    internal Task<AxiosResponse> SendPreparedAsync(AxiosConfig config, Type? dataType)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(config, dataType, target: null);
    }
}
