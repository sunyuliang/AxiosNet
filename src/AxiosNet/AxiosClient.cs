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
    // 0/1 一次性释放标志（Interlocked），重复 Dispose 直接返回。
    private int _disposed;
    // 在途请求数：释放时等它归零，避免打断正在用连接池的请求。
    private int _inFlight;
    // 池 / Handler 的释放标志：先拒绝新请求，等尾款归零再释放资源。
    private int _resourcesDisposed;

    /// <summary>
    /// 本实例的尝试管线：构造期编译成单个委托并固化，此后不可变。
    /// </summary>
    private readonly AxiosAttemptHandler _attemptPipeline;

    /// <summary>在途并发闸门（<c>Resilience.Concurrency.MaxInFlight</c>）：首个受限请求时按当时上限创建并固定，之后改上限会抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    private ConcurrencyGate? _gate;

    /// <summary>
    /// 备用端点的健康记忆：跨请求共享，主端点持续不可用时不必每条请求都先撞它一次。
    /// 惰性创建（没配备用端点就不会分配）。
    /// </summary>
    private FailoverHealth? _failoverHealth;

    /// <summary>
    /// 对应 axios instance.defaults。连接级选项（WithCredentials / MaxRedirects / Proxy / Credentials / PoolOptions）
    /// 构造时已固化，改后不生效；<c>Resilience.Concurrency.MaxInFlight</c> 的闸门在首个受限请求时创建，此后改会抛 <c>ERR_BAD_OPTION_VALUE</c>。
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

        // 每个实例一份序列化注册表：登记只影响本实例，全局 AxiosContent.Register 继续作为兜底。
        // 配置里自带一份时沿用它（DI 的 configure 回调可以预先放好）。
        _ = Defaults.Serialization.BodySerializers;

        // 下面的分支有提前 return，管线先在此初始化。
        AttemptPipeline = pipeline ?? AxiosAttemptPipeline.Default;
        _attemptPipeline = AttemptPipeline.Compile(SendTerminalAsync, Defaults);

        if (handler is not null)
        {
            if (pool is not null)
            {
                throw AxiosException.InvalidOption("不能同时注入 handler 和 pool：注入 handler 时不使用连接池，请二选一。", Defaults);
            }

            if (poolOptions is not null || Defaults.TransportOrNull?.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时注入 handler 和连接池选项（poolOptions / defaults.Transport.Pool）："
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
            if (poolOptions is not null || Defaults.TransportOrNull?.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 pool 和 poolOptions / defaults.Transport.Pool："
                    + "共享池已按自己的 ConnectionPoolOptions 创建，请在创建池时配置，或改为只传 poolOptions 让本实例自建池。", Defaults);
            }

            // 共享池的连接行为已固定，defaults 上的同类选项不生效。
            ValidateConnectionLevelOptions(defaults, Defaults, ConnectionLevelMessages.SharedPool);

            _pool = pool;
            _ownsPool = false;
            // 池实际生效的出口回写 Defaults：getter 与真实连接行为一致。
            SyncConnectionDefaults();
        }
        else
        {
            // 两处都配时直接报错。
            if (poolOptions is not null && Defaults.TransportOrNull?.Pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 defaults.Transport.Pool：两者描述的是同一个自建连接池，请只在一处配置。", Defaults);
            }

            var effective = (poolOptions ?? Defaults.TransportOrNull?.Pool ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期带配置抛错。
            HttpConnectionPool.ValidateOptions(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向，> 0 = 覆盖池默认上限；按 *Set 判断是否显式配置。
            if (Defaults.MaxRedirectsSet)
            {
                var maxRedirects = Defaults.TransportOrNull?.MaxRedirects ?? AxiosConfig.DefaultMaxRedirects;

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
                if (poolOptions.Proxy is not null && Defaults.TransportOrNull?.Proxy is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Proxy 和 defaults.Proxy 上配置代理：请只在一处设置。", Defaults);
                }

                if (poolOptions.Credentials is not null && Defaults.TransportOrNull?.Credentials is not null)
                {
                    throw AxiosException.InvalidOption("不能同时在 poolOptions.Credentials 和 defaults.Credentials 上配置凭据：请只在一处设置。", Defaults);
                }
            }

            effective.Proxy ??= Defaults.TransportOrNull?.Proxy;
            effective.Credentials ??= Defaults.TransportOrNull?.Credentials;
            if (Defaults.TransportOrNull?.WithCredentials is true)
            {
                effective.UseCookies = true;
                effective.Credentials ??= CredentialCache.DefaultCredentials;
            }

            _pool = new HttpConnectionPool(effective);
            _ownsPool = true;
            // 自建池会把 defaults / poolOptions / profile 合成一份生效值：回写 Defaults，
            // 让 defaults.Proxy / Credentials / MaxRedirects / WithCredentials 的 getter 与实际一致。
            SyncConnectionDefaults();
        }
    }

    /// <summary>
    /// 连接层配置在构造期定型（之后改 defaults 不会重建池）：把池实际生效的出口写回 <see cref="Defaults"/>，
    /// 使 getter 反映真实行为；同时清掉 setter 留下的 <c>*Set</c> 标记——这些值来自「回写」而非调用方显式设置，
    /// 后续合并仍按「未设置」处理。
    /// </summary>
    private void SyncConnectionDefaults()
    {
        if (_pool is null)
        {
            return;
        }

        var effective = _pool.EffectiveOptions;
        var maxRedirects = effective.AllowAutoRedirect ? effective.MaxAutomaticRedirections : 0;

        // 池的出口与「未写过的 defaults」完全一致时不回写：让 Defaults 继续保持没有 Transport 分组。
        // 一旦回写，Defaults.Clone()（每次 MergeConfig 一次）就多复制一份 TransportSettings（约 160 B/请求）；
        // 而这份回写的值本来就等于 TransportSettings 的默认值（Proxy / Credentials 为 null、MaxRedirects 5、WithCredentials false），
        // getter 读到的结果完全一样。调用方自己写过 Transport 时（TransportOrNull 非空）仍照常回写，不能悄悄丢掉它的值。
        if (Defaults.TransportOrNull is null
            && effective.Proxy is null
            && effective.Credentials is null
            && !effective.UseCookies
            && maxRedirects == AxiosConfig.DefaultMaxRedirects)
        {
            return;
        }

        var transport = Defaults.Transport;
        transport.Proxy = effective.Proxy;
        transport.Credentials = effective.Credentials;
        transport.MaxRedirects = maxRedirects;
        transport.WithCredentials = effective.UseCookies;

        // 回写不是「显式设置」：清标记，避免被当成按请求写过的实例级选项。
        transport.MaxRedirectsSet = false;
        transport.WithCredentialsSet = false;
    }

    // ---- getUri：只组装 URL，不发请求（对应 axios.getUri） ----

    /// <summary>按本实例生效配置拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求，返回值已转义。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：实例级项（<c>BaseAddress</c> / <c>CancellationToken</c> / 连接级设置 / 并发上限）取自本实例，写在该对象上的值无效。</remarks>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 回放 response.Config 时实例级项取自本实例默认值。
        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.SerializationOrNull?.QueryEncoder, config: merged).AbsoluteUri;
    }

    /// <summary>按 Action 配置拼出最终请求 URL，不发请求；回调在实例默认值的副本上修改（与快捷方法同一模型）。</summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 直接拼生效配置，不走 MergeConfig：回调里显式写的 BaseAddress 需保留。
        var config = NewEffectiveConfig();
        configure(config);
        return UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.QueryEncoder, config: config).AbsoluteUri;
    }

    // ---- request(config) 入口 ----

    /// <summary>对应 axios(config)，返回非泛型响应（JSON 时 Data 为 JsonElement）。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：<c>BaseAddress</c> / <c>CancellationToken</c> / 连接级设置 / 并发上限一律取自本实例，写在该对象上的值无效。详见 README「回放 response.Config」。</remarks>
    public Task<AxiosResponse> RequestAsync(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(PreparePerCall(config), dataType: null);
    }

    /// <summary>对应 axios(config)，Data 反序列化为 T。</summary>
    /// <remarks>传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并：<c>BaseAddress</c> / <c>CancellationToken</c> / 连接级设置 / 并发上限一律取自本实例，写在该对象上的值无效。详见 README「回放 response.Config」。</remarks>
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

    /// <summary>
    /// 按目标类型发送：<paramref name="dataType"/> 为 <c>null</c> 时按内核默认形态交付（JSON 正文为 JsonElement），
    /// 为 <c>typeof(void)</c> 时只完成请求、不装配正文，其余类型按泛型入口同款规则反序列化。
    /// </summary>
    internal Task<AxiosResponse> SendInternalAsync(AxiosConfig config, Type? dataType, AxiosResponse? target)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendAsync(PreparePerCall(config), dataType, target);
    }

    /// <summary>
    /// 把调用方交进来的配置整理成本次请求的生效配置。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MergeConfig"/> 互逆、<b>不可合并</b>：本方法只处理库产出的已合并配置
    /// （<see cref="AxiosConfig.EffectiveMerged"/> 为 <c>true</c>，例如回放 <c>response.Config</c>），
    /// 把它重新落到本实例默认值上并保持 <c>EffectiveMerged = true</c>；
    /// 调用方传入的普通配置（<c>EffectiveMerged</c> 为 <c>false</c>）原样返回，留给管线阶段 1 的 <see cref="MergeConfig"/> 合并。
    /// 两处的判断条件方向相反，合并其中一个会改变标记在管线里的传播语义。
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
}
