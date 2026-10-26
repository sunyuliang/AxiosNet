using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AxiosNet.Circuit;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet;

/// <summary>
/// axios 风格的 HTTP 客户端实例（对应 JS 的 axios instance）：支持 baseURL / method / headers /
/// params / data / timeout / auth / responseType / validateStatus / 取消令牌 / 拦截器，
/// 默认走连接池复用连接。
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
    /// 在途并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）。懒创建：首个<b>受限</b>请求时
    /// 按当时的生效上限建出信号量并固定 —— 这样「构造之后再配默认值」只要在首个请求之前就仍然有效。
    /// 之后改上限会报错（见 <see cref="ResolveConcurrencyGate"/>），不静默沿用旧值。
    /// </summary>
    private ConcurrencyGate? _gate;

    /// <summary>
    /// 对应 axios instance.defaults。
    /// <para>
    /// 连接级选项（<c>WithCredentials</c> / <c>MaxRedirects</c> / <c>Proxy</c> / <c>Credentials</c> /
    /// <c>PoolOptions</c>）在<b>构造时已固化</b>进连接池 / Handler，构造后再修改不生效且不报错 ——
    /// 要改变它们请新建实例。其余配置（头、transform、超时等）随时可改。
    /// </para>
    /// <para>
    /// 另有一项不属于连接级选项、但同样有固化时点：<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>
    /// 的闸门在<b>首个受限请求</b>时创建（因此构造后再配默认值仍有效），此后改它会抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </para>
    /// </summary>
    public AxiosConfig Defaults { get; }

    /// <summary>对应 axios instance.interceptors。</summary>
    public AxiosInterceptors Interceptors { get; } = new();

    /// <summary>本实例的连接池；注入自定义 <see cref="HttpMessageHandler"/> 时为 <c>null</c>。</summary>
    public HttpConnectionPool? Pool => _pool;

    /// <summary>是否使用连接池（即未直接注入 Handler）。</summary>
    public bool UsesConnectionPool => _pool is not null;

    /// <param name="defaults">实例默认配置（对应 axios instance.defaults）。</param>
    /// <param name="pool">
    /// 共享连接池；与 <paramref name="poolOptions"/> / <paramref name="handler"/> 互斥（同时传入抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// 池的连接行为在创建时已固定，故 defaults 上的连接级选项同样报错，请在 <see cref="ConnectionPoolOptions"/> 上配置。
    /// </param>
    /// <param name="poolOptions">本实例自建连接池的选项。</param>
    /// <param name="handler">
    /// 直接注入的 Handler（自定义链、测试用内存 Handler）；注入后<b>不创建连接池</b>，连接级选项由它自己决定。
    /// 要保留池化请用 <see cref="ConnectionPoolOptions.HandlerFactory"/>。
    /// </param>
    /// <param name="disposeHandler">释放本实例时是否一并释放注入的 <paramref name="handler"/>。</param>
    public AxiosClient(
        AxiosConfig? defaults = null,
        HttpConnectionPool? pool = null,
        ConnectionPoolOptions? poolOptions = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true)
    {
        Defaults = defaults ?? new AxiosConfig();

        if (handler is not null)
        {
            if (pool is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时注入 handler 和 pool：注入 handler 时不使用连接池，请二选一。", Defaults);
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

            // 池的连接行为（重定向 / 代理 / 凭据 / Cookie）在各 origin 的 Handler 创建时已固定，
            // defaults 上的同类选项无从生效 —— 与其静默忽略，不如明确报错（与注入 handler 的策略一致）。
            ValidateConnectionLevelOptions(defaults, Defaults, ConnectionLevelMessages.SharedPool);

            _pool = pool;
            _ownsPool = false;
        }
        else
        {
            // 同时传入时静默取一个不如明确报错（共享池 / 注入 handler 两个分支同样抛错）
            if (poolOptions is not null && Defaults.ConnectionOrNull?.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 defaults.PoolOptions：两者描述的是同一个自建连接池，请只在一处配置。",
                    Defaults);
            }

            var effective = (poolOptions ?? Defaults.ConnectionOrNull?.PoolOptions ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期就带配置抛错（池自身的校验没有 config 可挂），
            // 而不是等池内部抛出 config 为 null 的同类异常。
            HttpConnectionPool.ValidateOptions(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向（对应 axios maxRedirects: 0），> 0 = 覆盖池默认上限。
            // 必须按 *Set 判断：MaxRedirects 未显式设置时读到的也是默认值 5，
            // 无条件生效会把调用方在 poolOptions 上配好的 MaxAutomaticRedirections 悄悄改回 5。
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

            // defaults 上的代理 / 凭据是自建池的合法输入（与共享池分支的报错策略不同），
            // 但两处都写时按请求校验比对的是 defaults，静默取一个会让写入方以为生效了
            if (poolOptions is not null)
            {
                if (poolOptions.Proxy is not null && Defaults.ConnectionOrNull?.Proxy is not null)
                {
                    throw AxiosException.InvalidOption(
                        "不能同时在 poolOptions.Proxy 和 defaults.Proxy 上配置代理：请只在一处设置。", Defaults);
                }

                if (poolOptions.Credentials is not null && Defaults.ConnectionOrNull?.Credentials is not null)
                {
                    throw AxiosException.InvalidOption(
                        "不能同时在 poolOptions.Credentials 和 defaults.Credentials 上配置凭据：请只在一处设置。",
                        Defaults);
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

    /// <summary>
    /// 连接级选项无法按 defaults 生效时的构造期校验：注入自定义 handler 时由 handler 决定，
    /// 共享池时由池决定 —— 两种场景判定相同（都看 defaults 上的显式设置），
    /// 只是归属方与补救文案不同，故按场景取文案、共用一套判定。
    /// </summary>
    private static void ValidateConnectionLevelOptions(
        AxiosConfig? defaults,
        AxiosConfig config,
        ConnectionLevelMessages messages)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.MaxRedirectsSet)
        {
            throw AxiosException.InvalidOption(messages.MaxRedirects, config);
        }

        if (defaults.WithCredentialsSet)
        {
            throw AxiosException.InvalidOption(messages.WithCredentials, config);
        }

        if (defaults.ConnectionOrNull?.Proxy is not null || defaults.ConnectionOrNull?.Credentials is not null)
        {
            throw AxiosException.InvalidOption(messages.ProxyCredentials, config);
        }
    }

    /// <summary>两种连接级场景的报错文案（判定相同、归属方不同）。</summary>
    private sealed record ConnectionLevelMessages(string MaxRedirects, string WithCredentials, string ProxyCredentials)
    {
        /// <summary>注入自定义 handler：连接级选项由 handler 决定。</summary>
        public static ConnectionLevelMessages Handler { get; } = new(
            "注入自定义 handler 时 MaxRedirects 由 handler 决定（重定向在连接层处理），请在 handler 上配置。",
            "注入自定义 handler 时 WithCredentials 由 handler 决定（Cookie 容器在连接层），请在 handler 上配置。",
            "注入自定义 handler 时 Proxy / Credentials 由 handler 决定，请在 handler 上配置。");

        /// <summary>共享连接池：连接级选项由池决定。</summary>
        public static ConnectionLevelMessages SharedPool { get; } = new(
            "共享连接池时 MaxRedirects 由池决定（重定向由各 origin 的 Handler 处理），"
            + "请在创建 ConnectionPoolOptions 时设置 AllowAutoRedirect / MaxAutomaticRedirections，"
            + "或不传 pool 让本实例自建池。",
            "共享连接池时 WithCredentials 由池决定（Cookie 容器在连接层），"
            + "请在创建 ConnectionPoolOptions 时设置 UseCookies / Credentials，"
            + "或不传 pool 让本实例自建池。",
            "共享连接池时 Proxy / Credentials 由池决定，"
            + "请在 ConnectionPoolOptions.Proxy / Credentials 上设置，或不传 pool 让本实例自建池。");
    }

    // ------------------------------------------------------------------
    // getUri：只组装 URL，不发请求（对应 axios.getUri）
    // ------------------------------------------------------------------

    /// <summary>
    /// 按<b>本实例的生效配置</b>拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求。
    /// 返回本实例实际会发出的形态（已转义），可直接用于签名与日志。
    /// <para>
    /// 传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按<b>回放语义</b>重新合并：
    /// 其中的实例级项（<c>BaseAddress</c> / 代理 / 池选项）取自本实例，与
    /// <see cref="RequestAsync(AxiosConfig)"/> 真正发出去的地址一致 —— 签名与日志不会与实际去向错位。
    /// </para>
    /// <para><see cref="System.Uri"/> 按 RFC 3986 归一化非保留字符（<c>%41</c>→<c>A</c>），与请求行一致。</para>
    /// </summary>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 一律按「本实例的生效配置」算：回放 response.Config 时其中的 BaseAddress 等实例级项
        // 由 MergeConfig 的 instanceSourced 门禁让位给本实例默认值，与 RequestAsync 实际发出的地址一致。
        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.SerializationOrNull?.ParamsSerializer, config: merged)
            .AbsoluteUri;
    }

    /// <summary>
    /// 按 Action 配置拼出最终请求 URL，不发请求。
    /// 回调在实例默认值的副本上修改（与快捷方法同一模型）：<c>c.BaseAddress</c> / <c>c.Url</c> /
    /// <c>c.Params</c> 直接生效，返回本实例会发出的形态。
    /// </summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 直接拼生效配置，不再回落到 GetUri(AxiosConfig)：那条路径会走 MergeConfig，
        // 而它的 instanceSourced 门禁会把回调里显式写的 BaseAddress 当成「来源实例的值」丢掉。
        // 请求头不参与 URL 拼装，因此不需要 ApplyRemoveHeaders。
        var config = NewEffectiveConfig();
        configure(config);
        return UrlBuilder.Build(
                config.BaseAddress,
                config.Url,
                config.Params,
                config.SerializationOrNull?.ParamsSerializer,
                config: config)
            .AbsoluteUri;
    }

    // ------------------------------------------------------------------
    // request(config) 入口
    // ------------------------------------------------------------------

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

        // 起点与快捷方法一致（见 NewEffectiveConfig）：回调在实例默认值的副本上增删改，
        // 删头 / 清空头集合都是真的生效，不会出现「快捷入口摘得掉、主入口静默没摘」。
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
    /// 把调用方交进来的配置整理成「本次请求的生效配置」：普通配置（按请求补丁）原样返回，由
    /// <see cref="SendAsync"/> 合并到实例默认值；带 <see cref="AxiosConfig.EffectiveMerged"/> 的生效配置
    /// （典型是回放 <see cref="AxiosResponse.Config"/>）在这里<b>直接合并</b> —— 合并只读 perCall、不写它，
    /// 不必先克隆一份只为「清标记让 SendAsync 走合并分支」。
    /// <para>
    /// <c>EffectiveMerged</c> 在这里（而不是 <c>MergeConfig</c> 里）置：标记含义是「本次请求已合并、
    /// SendAsync 不必再合并」，只对这条路径成立；<c>MergeConfig</c> 的其他调用方拿到的产物仍按未合并输入处理。
    /// </para>
    /// </summary>
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

    // ------------------------------------------------------------------
    // 核心请求管线
    // ------------------------------------------------------------------

    private const string DisposedMessage = "AxiosClient 已释放，不能发起新请求";

    /// <summary>最后一个在途请求结束时，补做 <see cref="Dispose"/> 推迟的资源释放。</summary>
    private void ReleaseInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) == 1)
        {
            DisposeResources();
        }
    }

    /// <summary>
    /// 阶段 0 —— 入口守卫：先占一个在途名额，再复核释放标记（先占位再复核，不会出现
    /// 「拿着已释放的 Handler / 池跑到一半」的中间态）。
    /// <para>必须在调用方的 <c>try</c> 之外：它在名额还没占上时就抛，那时不该再归还。</para>
    /// </summary>
    private void EnterInFlight(AxiosConfig config)
    {
        // ObjectDisposedException.ThrowIf 是 .NET 7 才有的 API，手写以兼容 net6.0 目标。
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw DisposedError(config);
        }

        Interlocked.Increment(ref _inFlight);

        if (Volatile.Read(ref _disposed) == 1)
        {
            ReleaseInFlight();
            throw DisposedError(config);
        }
    }

    /// <summary>已释放实例上的请求统一报 <c>ERR_CANCELED</c>，不漏裸 <see cref="ObjectDisposedException"/>。</summary>
    private static AxiosException DisposedError(AxiosConfig config)
        => AxiosException.Canceled(config, null, DisposedMessage, new ObjectDisposedException(nameof(AxiosClient)));

    /// <summary>
    /// 请求入口：按固定顺序驱动整条管线 ——
    /// 合并 → 校验（实例级）→ 拦截 → 规范化 →（短路 | 校验 + 闸门 → 编码 → 发送）→ 收尾。
    /// <para>
    /// 顺序是契约（<c>ValidateRangeConfig</c> 在拦截器之后、闸门在校验之后、遥测起点在 URL 拼好之后、
    /// 短路在拦截器之后且在校验与闸门之前），每处的理由写在对应阶段方法上。
    /// <paramref name="target"/> 是调用方预造的响应对象，管线就地填充。
    /// </para>
    /// </summary>
    private async Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // 阶段 0：守卫。放在 try 之外 —— 它抛的时候名额还没占上，finally 里的归还不能跟着跑。
        EnterInFlight(perCallConfig);

        // 遥测状态全是局部量：关闭遥测时零分配；后三项在 URL 拼好之后才定（见阶段 5）。
        AxiosConfig? config = null;
        Uri? telemetryUri = null;
        var telemetryEnabled = false;
        var telemetryStart = 0L;
        Activity? telemetryActivity = null;

        try
        {
            // 阶段 1：合并 —— 得到本次请求的生效配置。
            config = MergeEffectiveConfig(perCallConfig);

            // 阶段 2：校验（实例级选项）—— 这几项构造期已固化进连接池，拦截器改不了（改了也不生效），
            // 所以判在拦截器之前，保证「拦截器看到的一定是发得出去的配置」。
            ValidatePerRequestOptions(config);

            // 阶段 3：拦截 —— 请求拦截器按注册逆序执行（与 axios 一致）。
            config = await InterceptRequestAsync(config).ConfigureAwait(false);

            // 阶段 4a：规范化 + 阶段 4b：范围校验 + 阶段 4c：短路 + 阶段 4d：闸门 —— 都必须看拦截器**之后**的配置：
            // 判早了就成了「校验旧配置、发新配置」，等于允许拦截器写 Timeout = -1 绕过护栏
            // （hasTimeout 随后为 false，静默不超时）；反过来也允许拦截器修正调用方的非法值。
            ApplyAutoResponseType(config, dataType);

            // 范围校验判的是「这份配置自身是否可执行」，与会不会真的发出请求无关：放在短路之前，
            // 让 Timeout = -1 这类错误总是报出来。否则同一个 bug 在网络路径报错、在缓存命中时静默通过
            // —— 错误报不报取决于缓存状态，是最难查的一类不一致。
            ValidateRangeConfig(config);

            // 闸门与编码看的是「这次 HTTP 操作」：短路不产生操作，并发名额 / 连接租约 / HTTP3 都无从谈起。
            if (config.ShortCircuit is { } shortCircuit)
            {
                return await CompleteShortCircuitAsync(shortCircuit, config, dataType, target)
                    .ConfigureAwait(false);
            }

            var gate = ResolveConcurrencyGate(config);

            // 阶段 5：编码 —— transformRequest → URL / 协议 → 正文 / 请求头。
            ApplyRequestTransforms(config);
            var plan = BuildRequestPlan(config);

            // 遥测起点：必须在这里，因为 span 要 URL（标签里的 host / path），且开关要看最终生效配置；
            // 在途数也只数「真的进了发送阶段」的请求 —— 准备阶段就失败的不是一次 HTTP 操作。
            telemetryUri = plan.Uri;
            if (AxiosTelemetry.IsEnabled(config))
            {
                telemetryEnabled = true;
                telemetryStart = AxiosTelemetry.Timestamp;
                AxiosTelemetry.EnterRequest();
                telemetryActivity = AxiosTelemetry.StartRequest(config, plan.Uri);
            }

            var template = CreateRequestMessage(config, plan, telemetryActivity);

            // 模板持有请求体（可能是调用方的 FileStream / FormFile），所有路径都必须先释放它。
            try
            {
                // 阶段 6：发送 —— 响应拦截器链 + 重试 / 熔断（重试在链内，见 SendWithRetryAsync）。
                var (result, streamOwner) = await RunResponseInterceptorsAsync(
                        template, SendContext.From(config, plan.Uri, dataType, target, gate))
                    .ConfigureAwait(false);

                // 拦截器返回了新实例：原流式正文已无引用，需要在这里释放。
                if (streamOwner is not null && !ReferenceEquals(result.Data, streamOwner))
                {
                    streamOwner.Dispose();
                }

                if (telemetryEnabled)
                {
                    // 必须在 template.Dispose() 之前（外层 finally）：那之后正文头已不可读。
                    AxiosTelemetry.RequestSucceeded(config, plan.Uri, result, telemetryStart, telemetryActivity);
                }

                return result;
            }
            finally
            {
                // 正文随模板一起释放；重试换出来的请求只摘不释放（见 SendRetryLoopAsync 的 finally）。
                template.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 遥测不该改变异常本身：记完原样抛（连栈都不动）。
            if (telemetryEnabled)
            {
                AxiosTelemetry.RequestFailed(config, telemetryUri, ex, telemetryStart, telemetryActivity);
            }

            throw;
        }
        finally
        {
            if (telemetryEnabled)
            {
                AxiosTelemetry.ExitRequest();
            }

            // 必须收 span：否则它会一直挂在 Activity.Current 上，后续请求的 span 会错挂到它下面。
            AxiosTelemetry.StopRequest(telemetryActivity);

            ReleaseInFlight();
        }
    }

    /// <summary>
    /// 阶段 1 —— 合并：把按请求配置落到实例默认值上。
    /// <para>已是合并产物的配置（<see cref="AxiosConfig.EffectiveMerged"/>）直接用：二次合并会把按请求项
    /// 当成实例默认项再走一遍。</para>
    /// </summary>
    private AxiosConfig MergeEffectiveConfig(AxiosConfig perCallConfig)
        => perCallConfig.EffectiveMerged ? perCallConfig : MergeConfig(perCallConfig);

    /// <summary>
    /// 阶段 3 —— 拦截：请求拦截器按注册逆序执行（与 axios 一致），返回最终生效配置。
    /// <para>
    /// 两种「空返回」就地报错（返回 null Task 会在 await 处炸成裸 NRE，返回 null 配置会让下一层拿到 null）；
    /// 返回 <see cref="ValueTask{TResult}"/> 是为了没有拦截器时不为此建一个只装 config 的 Task。
    /// </para>
    /// </summary>
    private async ValueTask<AxiosConfig> InterceptRequestAsync(AxiosConfig config)
    {
        foreach (var interceptor in Interceptors.Request.SnapshotReversed())
        {
            AxiosConfig next;
            try
            {
                // 返回 null Task 与下面返回 null 结果同罪：提前给出可定位的错误，
                // 不让 await null 炸成裸 NullReferenceException（同步结果请用 Task.FromResult）
                var pending = interceptor.OnFulfilled(config)
                    ?? throw AxiosException.InvalidOption(
                        "请求拦截器返回了 null Task：OnFulfilled / OnRejected 必须返回 Task<AxiosConfig> 实例"
                        + "（同步结果请用 Task.FromResult 包装）。",
                        config);
                next = await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (interceptor.OnRejected is null)
                {
                    throw;
                }

                next = await interceptor.OnRejected(ex).ConfigureAwait(false);
            }

            // 返回 null 会在下一行炸成裸 NullReferenceException，调用方看不出是哪层拦截器出的错。
            config = next ?? throw AxiosException.InvalidOption(
                "请求拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosConfig 实例"
                + "（要做修改请改传入的 config 后端返回它）。",
                config);
        }

        return config;
    }

    /// <summary>
    /// 阶段 4a —— 规范化：泛型 <c>&lt;dynamic&gt;</c> / <c>&lt;object&gt;</c> 与 <c>&lt;Stream&gt;</c> 的响应形态
    /// 自动切换（对标 responseType: 'dynamic' / 'stream'）。判据是 <see cref="AxiosConfig.ResponseTypeSet"/>
    /// 而非当前值（Json 是默认值，只看值分不清「没表态」）；位置必须在拦截器之后（拦截器可整体替换 config）。
    /// 切换本身不置该标记，否则回放生效配置时会被当成调用方显式意图。
    /// </summary>
    private static void ApplyAutoResponseType(AxiosConfig config, Type? dataType)
    {
        if (config.ResponseTypeSet)
        {
            return;
        }

        if (dataType == typeof(object))
        {
            config.SetResponseTypeAuto(ResponseType.Dynamic);
        }
        else if (dataType == typeof(Stream))
        {
            config.SetResponseTypeAuto(ResponseType.Stream);
        }
    }

    /// <summary>
    /// 阶段 5a —— 编码：<c>transformRequest</c> 依次作用于数据（拦截器之后、序列化之前）。
    /// <para>先判空：链延迟创建，空链不值得为一次枚举建出集合与快照。</para>
    /// </summary>
    private static void ApplyRequestTransforms(AxiosConfig config)
    {
        if (config.TransformRequestSnapshot is not { } requestTransforms)
        {
            return;
        }

        foreach (var transform in requestTransforms)
        {
            try
            {
                config.Data = transform(config.Data, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                throw AxiosException.BadRequestPayload(config, null, "transformRequest", ex);
            }
        }
    }

    /// <summary>
    /// 一次请求的「成型结果」：最终 URL 与协议协商参数。三样必须一起算（h3 校验要同时看
    /// URL 的 scheme 与版本策略）。结构体：热路径上不多一次堆分配。
    /// </summary>
    private readonly struct RequestPlan
    {
        public RequestPlan(Uri uri, Version version, HttpVersionPolicy versionPolicy)
        {
            Uri = uri;
            Version = version;
            VersionPolicy = versionPolicy;
        }

        /// <summary>最终请求 URL（已合并 baseURL / params 并转义）。</summary>
        public Uri Uri { get; }

        /// <summary>请求的 HTTP 版本（默认 2.0，理由见 <see cref="BuildRequestPlan"/>）。</summary>
        public Version Version { get; }

        /// <summary>版本协商策略（默认 <see cref="HttpVersionPolicy.RequestVersionOrLower"/>）。</summary>
        public HttpVersionPolicy VersionPolicy { get; }
    }

    /// <summary>
    /// 阶段 5b —— 成型：拼出最终 URL、确定协议协商参数，并做 HTTP/3 的前置校验。
    /// <para>
    /// 默认 HTTP/2 + <see cref="HttpVersionPolicy.RequestVersionOrLower"/>：h2 已是公网流量主力，
    /// 而 OrLower 的降级发生在 TLS 的 ALPN 里（不多往返、不多连接），老服务照样谈成 1.1；
    /// 策略显式写出来是因为「不支持就降级」是库的契约，不能随 BCL 默认值变化而静默失效。
    /// 明文 http:// 没有 ALPN，一律 1.1 —— 默认值只影响 https。要强制 1.1 写 Version11，
    /// 要 h3 写 Version30（本机能力见 <see cref="Http3Support"/>，协商与降级后的观测见 README）。
    /// </para>
    /// <para>h3 校验放在遥测起点之前：那是「准备阶段就失败」的配置错误，不是一次 HTTP 操作。</para>
    /// </summary>
    private static RequestPlan BuildRequestPlan(AxiosConfig config)
    {
        var uri = UrlBuilder.Build(
            config.BaseAddress,
            config.Url,
            config.Params,
            config.SerializationOrNull?.ParamsSerializer,
            config: config);

        var connection = config.ConnectionOrNull;
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version20;
        var versionPolicy = connection?.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower;

        // HTTP/3 的前置校验：只在真的要 h3 时走（h2 / 1.1 请求一次 Major 比较就返回）。
        ValidateHttp3Request(uri, httpVersion, versionPolicy, config);

        return new RequestPlan(uri, httpVersion, versionPolicy);
    }

    /// <summary>
    /// 阶段 5c —— 编码：建请求模板（正文 + 请求头）。正文只准备一次：重试要换新的
    /// <see cref="HttpRequestMessage"/>（同一实例发不了两次），但正文继续复用 —— 上传进度与限速状态都挂在
    /// 正文上，换一份等于进度清零、白序列化一遍。成功返回时模板所有权交给调用方；内部失败自己释放。
    /// </summary>
    private static HttpRequestMessage CreateRequestMessage(
        AxiosConfig config,
        RequestPlan plan,
        Activity? telemetryActivity)
    {
        var template = new HttpRequestMessage(config.Method, plan.Uri)
        {
            Version = plan.Version,
            VersionPolicy = plan.VersionPolicy
        };

        try
        {
            HttpContent? content;
            try
            {
                content = RequestBodyFactory.Create(config.Data, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // 序列化失败同样走统一错误模型；取消类异常原样抛出，保留 IsCancel 语义。
                // 声明式表单的 config.Data 只是内部表单字典：报请求模型名，别把 Dictionary`2 这种实现细节漏给调用方。
                throw AxiosException.BadRequestPayload(
                    config,
                    template,
                    config.Data is DeclaredForm declared
                        ? declared.SourceName ?? declared.GetType().Name
                        : config.Data?.GetType().Name ?? "null",
                    ex);
            }

            // 上传进度 / 限速：包一层流量统计（头部复制，序列化逻辑不变）
            var callbacks = config.CallbacksOrNull;
            var maxRate = config.LimitsOrNull?.MaxRate;
            if (content is not null && (callbacks?.OnUploadProgress is not null || maxRate is > 0))
            {
                content = new ProgressHttpContent(content, callbacks?.OnUploadProgress, maxRate, config);
            }

            if (content is not null)
            {
                if (config.LimitsOrNull?.MaxBodyLength is long maxBody
                    && content.Headers.ContentLength is long bodyLength
                    && bodyLength > maxBody)
                {
                    content.Dispose();
                    throw AxiosException.MaxBodyLength(config, template, bodyLength, maxBody);
                }

                template.Content = content;

                // 长度已知时顺手写进 span；未知（chunked / 不可定位流）就不写，不猜。
                AxiosTelemetry.SetRequestBodySize(telemetryActivity, content.Headers.ContentLength);
            }

            ApplyHeaders(template, config);
            return template;
        }
        catch
        {
            template.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 阶段 6 —— 发送：重试 / 熔断的发送循环，外面按注册顺序套上响应拦截器责任链。
    /// <para>
    /// 重试在链内：拦截器只看到最终结果，不会因重试被重复执行。返回的 <c>StreamOwner</c> 是流式正文的
    /// 所有权凭证 —— 成功路径交调用方兜底（拦截器可能换掉响应），失败路径在这里就地释放。
    /// </para>
    /// </summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> RunResponseInterceptorsAsync(
        HttpRequestMessage template,
        SendContext ctx)
    {
        var responseInterceptors = Interceptors.Response.Snapshot();

        if (responseInterceptors.Length == 0)
        {
            // 空链：把发送的 Task 原样交回去，省掉闭包 + 委托 + 责任链 + 状态机（热路径）。
            // 这条路径没有能抛异常的拦截器，也就不需要兜底释放。
            return SendWithRetryAsync(template, ctx);
        }

        return RunResponseChainAsync(template, ctx, responseInterceptors);
    }

    /// <summary>
    /// 响应拦截器责任链（只在有拦截器时走到）：按注册顺序包装，最内层是真正的发送。
    /// <para>链内任何一层抛异常都要把流式正文的租约还回去，否则该 origin 节点再也不会被驱逐。</para>
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> RunResponseChainAsync(
        HttpRequestMessage template,
        SendContext ctx,
        ResponseInterceptor[] responseInterceptors)
    {
        // 流式正文的所有权：拦截器抛异常或丢弃响应时由这里兜底释放。
        IDisposable? streamOwner = null;

        try
        {
            // 责任链的末端：真正发送（重试 / 熔断在这一层里面）
            Func<Task<AxiosResponse>> invoker = async () =>
            {
                var (invoked, owner) = await SendWithRetryAsync(template, ctx).ConfigureAwait(false);

                streamOwner = owner;
                return invoked;
            };

            var chain = WrapWithInterceptors(invoker, responseInterceptors, ctx.Config);
            return (await chain().ConfigureAwait(false), streamOwner);
        }
        catch
        {
            try
            {
                streamOwner?.Dispose();
            }
            catch
            {
                // 兜底释放失败不能掩盖拦截器的原始异常
            }

            throw;
        }
    }

    /// <summary>
    /// 把拦截器按注册顺序套在末端调用外面，最内层是「真正产生响应的那一步」。
    /// <para>
    /// 网络路径与短路路径共用这一份责任链：短路响应若绕过拦截器，「命中缓存」就成了对调用方可见的行为差异
    /// —— 而拦截器正是统一拆包、加解密、日志所在的地方。返回 <c>null</c> 就地报错，不让它传到调用方远处炸 NRE。
    /// </para>
    /// </summary>
    private static Func<Task<AxiosResponse>> WrapWithInterceptors(
        Func<Task<AxiosResponse>> invoker,
        ResponseInterceptor[] responseInterceptors,
        AxiosConfig config)
    {
        var chain = invoker;

        foreach (var interceptor in responseInterceptors)
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

                    // 返回 null 会被当成「响应就是 null」交回调用方，GetAsync<T> 随后炸 NRE —— 明确报错。
                    return (await rejected(ex).ConfigureAwait(false))
                        ?? throw NullInterceptorResult(config);
                }

                return (await fulfilled(response).ConfigureAwait(false))
                    ?? throw NullInterceptorResult(config);
            };
        }

        return chain;
    }

    /// <summary>
    /// 阶段 4c —— 短路：交付调用方给的响应，本次请求<b>不发送</b>。
    /// <para>
    /// 保留的只有「响应方向」的语义：按本次调用的类型反序列化、跑 transformResponse、过
    /// <see cref="AxiosConfig.CallbackSettings.ValidateStatus"/>、过响应拦截器 —— 调用方看不出这与一次真实响应
    /// 有什么区别（唯一可辨的痕迹是 <see cref="AxiosResponse.Request"/> 为 <c>null</c>）。跳过的都是
    /// 「一次 HTTP 操作」本身：并发闸门、编码、连接租约、发送、重试、熔断、遥测 span。
    /// </para>
    /// <para>
    /// 范围校验不在此列 —— 它在进来之前就跑完了（<see cref="ValidateRangeConfig"/>）：那判的是配置自身是否可执行，
    /// 与会不会真的发出请求无关。
    /// </para>
    /// </summary>
    private async Task<AxiosResponse> CompleteShortCircuitAsync(
        AxiosShortCircuit reply,
        AxiosConfig config,
        Type? dataType,
        AxiosResponse? target)
    {
        // 没有 I/O 也尊重调用方的取消意图：同一个调用点，短路与否不该改变「已取消」的结论。
        if (config.CancellationToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(config, null);
        }

        var validateStatus = config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
        var statusAccepted = validateStatus(reply.Status);

        // 状态码不合法时按宽松口径解析（与网络路径同口径）：404 + HTML 也能带着 Status/Response 抛错，
        // 而不是先在反序列化处炸成 ERR_BAD_RESPONSE 把两者全丢。
        var response = ResponseReader.ReadShortCircuit(reply, config, dataType, target, lenient: !statusAccepted);

        // 末端：不合法状态码在这里抛 —— 放在链内才能被拦截器的 OnRejected 接住（与网络路径一致）。
        Task<AxiosResponse> Terminal()
        {
            if (!statusAccepted)
            {
                throw AxiosException.FromResponse(config, null, response);
            }

            return Task.FromResult(response);
        }

        var responseInterceptors = Interceptors.Response.Snapshot();
        if (responseInterceptors.Length == 0)
        {
            // 空链：直接交付，不为「一次都不包装」建委托链（与发送路径同一条优化）。
            return await Terminal().ConfigureAwait(false);
        }

        var chain = WrapWithInterceptors(Terminal, responseInterceptors, config);
        return await chain().ConfigureAwait(false);
    }

    /// <summary>
    /// 一次请求在「发送阶段」不变的输入（模板之外的全部），打成结构体收敛五个发送方法的十来个人参：
    /// 它们全是「看起来都能互换」的引用与令牌，按位置传错的代价是静默发错请求或漏还资源。
    /// 值传递、栈上复制，不产生堆分配。
    /// <para>
    /// 不含 <see cref="HttpRequestMessage"/>：它逐次尝试变（重试必须换新实例，见 <see cref="CloneRequest"/>），
    /// 混进来会让「这一次用的是哪条请求」变得不明确。
    /// </para>
    /// </summary>
    private readonly struct SendContext
    {
        private SendContext(
            AxiosConfig config,
            Uri uri,
            CancellationToken externalToken,
            Type? dataType,
            AxiosResponse? target,
            bool streamResponse,
            AxiosConfig.RetrySettings? retry,
            AxiosConfig.CircuitSettings? circuit,
            RetryBody? bodyRewind,
            ConcurrencyGate? gate)
        {
            Config = config;
            Uri = uri;
            ExternalToken = externalToken;
            DataType = dataType;
            Target = target;
            StreamResponse = streamResponse;
            Retry = retry;
            Circuit = circuit;
            BodyRewind = bodyRewind;
            Gate = gate;
        }

        /// <summary>
        /// 唯一创建入口：派生项（令牌 / 流式标记 / 重试与熔断配置 / 正文可重放性）在这里一次算清，
        /// 调用点只交「生效配置 + 本次请求的外部输入」。
        /// </summary>
        public static SendContext From(
            AxiosConfig config,
            Uri uri,
            Type? dataType,
            AxiosResponse? target,
            ConcurrencyGate? gate)
        {
            var retry = config.RetryOrNull;

            return new SendContext(
                config,
                uri,
                config.CancellationToken,
                dataType,
                target,
                config.ResponseType == ResponseType.Stream,
                retry,
                config.CircuitOrNull,

                // 正文可重放性：重试复用同一份正文，来自流的正文必须能拨回起点，
                // 否则第二次发送只会发出空内容 —— 在发请求之前就拦下，而不是等重试时才发现。
                retry?.Policy is { MaxRetries: > 0 } ? RetryBody.Prepare(config) : null,
                gate);
        }

        /// <summary>本次请求的生效配置（合并 + 拦截器之后的最终形态）。</summary>
        public AxiosConfig Config { get; }

        /// <summary>最终 URL：错误上下文与遥测标签都用它。</summary>
        public Uri Uri { get; }

        /// <summary>调用方令牌：贯穿所有尝试与退避等待。</summary>
        public CancellationToken ExternalToken { get; }

        /// <summary>泛型入口的目标类型（非泛型入口为 <c>null</c>），决定反序列化成什么。</summary>
        public Type? DataType { get; }

        /// <summary>调用方预造的响应对象：管线就地填充，省掉一次类型转换与再分配。</summary>
        public AxiosResponse? Target { get; }

        /// <summary>是否流式响应（<c>ResponseType == Stream</c>）。</summary>
        public bool StreamResponse { get; }

        /// <summary>重试配置：<c>null</c> = 不重试（默认）。</summary>
        public AxiosConfig.RetrySettings? Retry { get; }

        /// <summary>熔断配置：<c>null</c> = 不熔断（默认）。</summary>
        public AxiosConfig.CircuitSettings? Circuit { get; }

        /// <summary>正文可重放性检查：只在「真的会重试」时才建，否则为 <c>null</c>。</summary>
        public RetryBody? BodyRewind { get; }

        /// <summary>并发闸门：<c>null</c> = 本次不限并发。</summary>
        public ConcurrencyGate? Gate { get; }
    }

    /// <summary>
    /// 带重试的发送入口：没有策略时<b>零开销</b>——直接交回单次发送的 Task，
    /// 不为「一次都不重试」的循环建状态机。
    /// </summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithRetryAsync(
        HttpRequestMessage template,
        SendContext ctx)
    {
        var policy = ctx.Retry?.Policy;
        if (policy is null || policy.MaxRetries <= 0)
        {
            return SendAttemptAsync(template, ctx);
        }

        return SendRetryLoopAsync(template, ctx, policy);
    }

    /// <summary>
    /// 重试循环（只在配置了策略时走到）。每次尝试换一条新请求（同一实例不能发两次），正文与请求头沿用模板；
    /// 每次尝试有<b>独立的超时预算</b>（<see cref="AxiosConfig.Timeout"/> 是单次尝试上限，不是总预算），
    /// 调用方令牌则贯穿所有尝试与等待。
    /// <para>
    /// 熔断闸门在每次尝试之前（见 <see cref="SendAttemptAsync"/>）：熔断一旦打开，下一次尝试被立刻拦下，
    /// 不再白等一轮退避 —— 这正是「重试 + 熔断」一起开的意义。
    /// </para>
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendRetryLoopAsync(
        HttpRequestMessage template,
        SendContext ctx,
        IAxiosRetryPolicy policy)
    {
        var retry = ctx.Retry!;
        var externalToken = ctx.ExternalToken;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // 同一份正文再发一次：来自流的正文要先把读指针拨回起点。
                // 放在 try 外面是有意的 —— 拨不动（流被关闭 / 不支持定位）说明正文已不可重放，
                // 这不是「一次失败的尝试」，不该再拿去问策略要不要重试。
                ctx.BodyRewind?.Rewind(ctx.Config);
            }

            // 首次尝试直接用模板，后续尝试按模板复制（模板本身不能再发，也不该被后续尝试改写）
            var request = attempt == 1 ? template : CloneRequest(template);
            try
            {
                return await SendAttemptAsync(request, ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消类异常不在这里接：它不该走「问策略要不要重试」这条路，
                // 直接冒到调用方（SendCoreAsync 已把超时/取消翻译成 AxiosException，
                // 能漏到这里的裸 OperationCanceledException 只可能来自调用方取消）。
                var failed = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, ctx.Config, (ex as AxiosException)?.Response);

                if (!CanRetry(failed, externalToken, policy))
                {
                    throw;
                }

                var delay = policy.GetDelay(failed);
                if (delay < TimeSpan.Zero)
                {
                    // 策略算错时间不该把请求变成配置错误：负值按「不等待」处理
                    delay = TimeSpan.Zero;
                }
                else if (delay > Timeouts.Max)
                {
                    // 与 Timeout 同一个坑：Task.Delay 底层是计时器队列，上限 uint.MaxValue-1 毫秒，
                    // 超出会抛裸异常。内置策略有配置期校验兜着，自定义策略可以返回任意值 —— 在这里收敛。
                    delay = Timeouts.Max;
                }

                // 回调与遥测共用同一个上下文
                var retryContext = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, ctx.Config, failed.Response, delay);

                // 回调在 catch 块里调用（不是 try 里）：它抛出的异常不会被当成「本次尝试失败」
                // 再去问一次策略，而是按 RetrySettings.OnRetry 的约定原样抛给调用方。
                retry.OnRetry?.Invoke(retryContext);

                // 重试计数 / span 的 resend_count 与 retry 事件：观测点放在「决定重试」这一刻，
                // 与 OnRetry 回调同一语义（不含「次数到顶后的最后一次失败」）。
                AxiosTelemetry.RequestRetried(retryContext, ctx.Uri);

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, externalToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce)
                    {
                        // 等待期间被取消：仍按统一错误模型报「已取消」，而不是漏出裸 TaskCanceledException
                        throw AxiosException.Canceled(ctx.Config, request, "请求在重试等待期间被取消", oce);
                    }
                }
            }
            finally
            {
                if (!ReferenceEquals(request, template))
                {
                    // 正文是共享的（后续尝试还要用）：先摘下来，只释放这条请求本身
                    request.Content = null;
                    request.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 一次尝试的入口：先过熔断闸门，再发送。没有熔断策略时直接转给 <see cref="SendCoreAsync"/>
    /// （多一层方法调用，不建状态机）：不开熔断的请求与没有这个功能时完全一致。
    /// </summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendAttemptAsync(
        HttpRequestMessage request,
        SendContext ctx)
    {
        if (ctx.Circuit?.Policy is not { } policy)
        {
            return SendCoreAsync(request, ctx);
        }

        return SendWithCircuitAsync(request, ctx, policy);
    }

    /// <summary>
    /// 带熔断的单次尝试：放行 → 发送 → 回报结果。拒绝时<b>不发请求</b>（不建连、不占租约），直接抛
    /// <c>ERR_CIRCUIT_OPEN</c>。回报必须恰好一次 —— 漏掉「取消」这一支会让熔断器卡在半开，永远等不到恢复。
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithCircuitAsync(
        HttpRequestMessage request,
        SendContext ctx,
        IAxiosCircuitPolicy policy)
    {
        // 调用方已取消就先报取消：熔断拒绝只是一句「没发出去」，会把真正的退出原因（取消）盖掉。
        if (ctx.ExternalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(ctx.Config, request);
        }

        var context = new AxiosCircuitContext(CircuitHost(ctx.Uri), ctx.Uri, ctx.Config);

        if (!policy.ShouldAllow(context))
        {
            // 建议等待时间：策略说不准时给零，错误消息里就不带「retry after」。
            var retryAfter = policy.GetRetryAfter(context);

            // 回调在抛错之前调用：它抛出的异常按约定原样抛给调用方，不会被当成「下游故障」去改熔断状态
            // （此刻请求根本没发出去）。上下文里补上建议等待，回调不必为了写日志去 catch 异常。
            ctx.Circuit!.OnRejected?.Invoke(
                new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, retryAfter: retryAfter));

            throw AxiosException.CircuitOpen(ctx.Config, request, context.Host, retryAfter);
        }

        AxiosResponse response;
        IDisposable? streamOwner;

        try
        {
            (response, streamOwner) = await SendCoreAsync(request, ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 失败结论要带上异常：策略据此区分「下游故障」（网络 / 超时 / 5xx）与「取消 / 4xx」。
            // 这里不接策略异常 —— 它按约定原样抛给调用方（并掩盖本次尝试的原始失败）。
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, ex));
            throw;
        }

        // 走到这里说明响应已被 validateStatus 接受，但仍可能是 5xx（validateStatus 放行时没有异常）——
        // 把状态码一并回报，熔断才能与「非 2xx 抛异常」那条路记同一本账。
        // 回报放在 try 之外：策略抛出的异常不该被上面那个 catch 当成「本次尝试失败」再记一笔。
        try
        {
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, statusCode: response.Status));
        }
        catch
        {
            // 策略异常按约定原样抛给调用方，但不能顺带漏掉流式正文的租约 —— 它占着该 origin 的并发额度。
            streamOwner?.Dispose();
            throw;
        }

        return (response, streamOwner);
    }

    /// <summary>
    /// 熔断的计数维度：<c>host:port</c>（同一 host 的不同端口是两个独立下游）。
    /// 非分层 URI 退化为 host，再退化为空串 —— 空 key 也能正常计数。
    /// </summary>
    private static string CircuitHost(Uri uri)
        => uri.Authority is { Length: > 0 } authority ? authority : uri.Host;

    /// <summary>
    /// 库层面的「不该重试」：次数到顶、调用方已取消、错误本身就是取消、被熔断拦下；其余交给策略判断。
    /// </summary>
    private static bool CanRetry(AxiosRetryContext context, CancellationToken externalToken, IAxiosRetryPolicy policy)
    {
        if (context.Attempt > policy.MaxRetries)
        {
            return false;
        }

        if (externalToken.IsCancellationRequested)
        {
            return false;
        }

        if (context.Exception is OperationCanceledException)
        {
            // 兜底：调用方取消了，或者异常链里带着取消（重试循环本身已经把裸取消挡在外面）
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCanceled })
        {
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCircuitOpen })
        {
            // 熔断拒绝不是「一次失败的尝试」（请求根本没发出去）：再重试只会在同一个开着的闸门上
            // 再撞一次，既不会成功，也把等待时间白白拉长。
            return false;
        }

        return policy.ShouldRetry(context);
    }

    /// <summary>
    /// 按模板复制一条可发送的请求（请求头逐个搬运，正文沿用同一份）。
    /// 用 <c>TryAddWithoutValidation</c> 而不是赋值：模板上的头已经过 <c>ApplyHeaders</c> 的校验与分流，
    /// 这里只做搬运，不再触发一次校验（也避免 BCL 对个别头的规范化改写）。
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage template)
    {
        var request = new HttpRequestMessage(template.Method, template.RequestUri)
        {
            Version = template.Version,

            // 策略必须一起搬：只搬 Version 会把 RequestVersionExact 悄悄变回默认的 OrLower
            // （重试的第 2 次起行为与第 1 次不一致，且没有任何报错）。
            VersionPolicy = template.VersionPolicy
        };

        foreach (var header in template.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = template.Content;
        return request;
    }

    /// <summary>
    /// 一次尝试的时间预算：超时令牌源 + 流式响应的剩余预算计时器。粒度定在「一次尝试」上，
    /// 「每次重试都有独立超时预算」才是自然结果，而不是从第一次尝试起算一次总预算。
    /// <para>
    /// 只有「有超时」才建独立令牌源：无超时时 <see cref="Token"/> 直接是调用方令牌，建 linked CTS
    /// 换不来任何行为差异，却要每请求付一次 CTS + 注册。结构体而非类：热路径不为此多一次堆分配。
    /// </para>
    /// </summary>
    private readonly struct AttemptBudget : IDisposable
    {
        private readonly CancellationTokenSource? _timeoutCts;
        private readonly CancellationToken _externalToken;
        private readonly Stopwatch? _clock;
        private readonly TimeSpan _timeout;

        private AttemptBudget(
            CancellationTokenSource? timeoutCts,
            CancellationToken externalToken,
            Stopwatch? clock,
            TimeSpan timeout)
        {
            _timeoutCts = timeoutCts;
            _externalToken = externalToken;
            _clock = clock;
            _timeout = timeout;
            Token = timeoutCts?.Token ?? externalToken;
        }

        /// <summary>交给底层每次 I/O 的令牌：无超时时就是调用方令牌（None 时不注册、不分配）。</summary>
        public CancellationToken Token { get; }

        /// <summary>
        /// 流式响应的剩余预算：只有「流式 + 有超时」才有值（其余组合下计时器纯属白分配）。
        /// 流式响应的 timeout 要延续到正文读完，所以记录起点算剩余。
        /// </summary>
        public TimeSpan? StreamRemaining => _clock is null ? null : _timeout - _clock.Elapsed;

        /// <summary>
        /// 取消类异常的归属：调用方取消 → <c>ERR_CANCELED</c>；否则就是本次尝试超时 → <c>ERR_TIMEOUT</c>。
        /// 租连接 / 发送 / 读正文三处判定条件完全一致，集中在这里以免其中一处漏判而把两者搞反。
        /// </summary>
        public AxiosException Translate(
            OperationCanceledException exception,
            AxiosConfig config,
            HttpRequestMessage request)
        {
            return !_externalToken.IsCancellationRequested && _timeoutCts is { IsCancellationRequested: true }
                ? AxiosException.Timeout(config, request, exception)
                : AxiosException.Canceled(config, request, exception);
        }

        public void Dispose() => _timeoutCts?.Dispose();

        /// <summary>按生效配置建预算：超时上限收敛到计时器可表示的范围（见 <see cref="Timeouts.Clamp"/>）。</summary>
        public static AttemptBudget Start(
            AxiosConfig config,
            CancellationToken externalToken,
            bool streamResponse)
        {
            var timeout = config.Timeout;
            var hasTimeout = timeout > TimeSpan.Zero;

            var timeoutCts = hasTimeout
                ? (externalToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                    : new CancellationTokenSource())
                : null;

            if (timeoutCts is not null)
            {
                // CancelAfter 上限约 49.7 天（uint.MaxValue-1 毫秒），越界抛裸 ArgumentOutOfRangeException：
                // 收敛到上限，语义等价于「极长但有限」。
                timeoutCts.CancelAfter(Timeouts.Clamp(timeout));
            }

            // 流式 + 有超时才用得上计时器（口径见 StreamRemaining）。
            var clock = streamResponse && hasTimeout ? Stopwatch.StartNew() : null;

            return new AttemptBudget(timeoutCts, externalToken, clock, timeout);
        }
    }

    /// <summary>
    /// 一次尝试的完整生命周期：占并发槽位 → 建超时预算 → 取连接 → 发送 → 读取 → 转换。
    /// 资源释放点全部收在 finally：并发槽位与连接租约都是一次尝试一份，漏还一次就等于永久少一个名额 / 一格连接，
    /// 且只表现为「越来越慢」，极难排查。唯一例外是流式响应 —— 租约、消息与槽位随正文流转交调用方（见阶段 6）。
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendCoreAsync(
        HttpRequestMessage request,
        SendContext ctx)
    {
        var config = ctx.Config;

        PoolLease? lease = null;

        // 流式正文的所有权随后随返回值交给调用方（拦截器抛异常时由调用方兜底释放）。
        IDisposable? streamOwner = null;

        // 并发槽位：与 lease 同一粒度、同一释放点。声明在 try 之外只为 finally 能看见它，
        // **取值在 try 之内** —— 占位成功之后任何一步失败都必须把名额还回去。
        IDisposable? slot = null;

        try
        {
            // 阶段 1：并发槽位 —— 先占在途名额，再建超时预算（排队时间不计入 Timeout）。
            slot = await EnterSlotAsync(request, ctx).ConfigureAwait(false);

            // 阶段 2：时间预算 —— 一次尝试一份超时令牌源。
            using var budget = AttemptBudget.Start(config, ctx.ExternalToken, ctx.StreamResponse);

            // 阶段 3：取连接 —— 注入 Handler 时固定用它，默认实例按 origin 租（连接复用 + 并发上限）。
            var (httpClient, leased) = await LeaseClientAsync(request, ctx, budget).ConfigureAwait(false);
            lease = leased;

            // 阶段 4：发送 —— 只到响应头，正文留给读取阶段。
            var responseMessage = await SendRequestAsync(httpClient, request, ctx, budget).ConfigureAwait(false);

            // 阶段 5：读取 —— 状态码接受性只判一次，它同时决定宽松解析与是否抛错。
            var (response, statusAccepted) = await ReadResponseAsync(responseMessage, request, ctx, budget)
                .ConfigureAwait(false);

            // 阶段 6：转换 —— 状态码 → 异常；流式正文 → 计时流 + 资源所有权转移。
            //
            // stream 模式下消息不能释放（正文流从它取出），所有权转移给 ResponseStreamOwner。
            if (!ctx.StreamResponse)
            {
                responseMessage.Dispose();
            }

            if (!statusAccepted)
            {
                if (ctx.StreamResponse)
                {
                    // 失败响应没人读正文：先把一段错误体抢救成文本（ApiErrorContext 的唯一来源），
                    // 再立即释放内容把连接还回池，否则这条连接会被一个永远不会被读取的流一直占住。
                    response.RawText = await ReadStreamErrorBodyAsync(
                            response.Data as Stream, config, budget.Token)
                        .ConfigureAwait(false);

                    (response.Data as IDisposable)?.Dispose();
                    response.Data = null;
                    responseMessage.Dispose();
                }

                throw AxiosException.FromResponse(config, request, response);
            }

            if (ctx.StreamResponse && response.Data is Stream body)
            {
                // 流式正文的超时口径：显式 StreamIdleTimeout 时「Timeout 只到响应头」，
                // 正文按空闲超时（或完全不限）；未设置时保持「整条流共享剩余预算」。
                Stream wrapped = config.LimitsOrNull?.StreamIdleTimeout is { } idleTimeout
                    ? new TimeoutStream(
                        body,
                        remaining: null,
                        ctx.ExternalToken,
                        config,
                        request,
                        idleTimeout > TimeSpan.Zero ? idleTimeout : null)
                    : new TimeoutStream(
                        body,
                        budget.StreamRemaining,
                        ctx.ExternalToken,
                        config,
                        request);

                // 租约与 HttpResponseMessage 一并转移给流：下载期间该 origin 仍算活跃，流 Dispose 时统一释放。
                // 并发槽位同理 —— 流式响应在调用方读正文的整段时间里仍算「在途」，提前归还等于让并发上限
                // 对下载失效（下载恰恰是最该被限的那类请求）。
                wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease, slot);
                lease = null;
                slot = null;

                response.Data = wrapped;
                streamOwner = wrapped;
            }

            return (response, streamOwner);
        }
        catch (ObjectDisposedException ex)
        {
            // 池被 Dispose()/Clear()（或在途时关闭）：底层资源已释放，
            // 裸 ObjectDisposedException 没有信息量，翻译成「请求被中止」。
            throw AxiosException.Canceled(
                config,
                request,
                "客户端或其连接池在请求在途时被释放，请求被中止",
                ex);
        }
        finally
        {
            lease?.Dispose();

            // 槽位在所有失败路径上都归还；流式成功路径已把它转交给正文流（slot = null）。
            slot?.Dispose();
        }
    }

    /// <summary>
    /// 阶段 1 —— 并发槽位：先占在途名额，再建超时预算 —— 排队时间不计入 <see cref="AxiosConfig.Timeout"/>
    /// （队列有自己的 <see cref="AxiosConfig.LimitSettings.QueueTimeout"/>）。放在「每次尝试」里是为了让
    /// 「每次重试各自取槽」成为自然结果：退避等待期间不占名额。返回值必须由调用方在 finally 归还（幂等）。
    /// </summary>
    private static async ValueTask<IDisposable?> EnterSlotAsync(HttpRequestMessage request, SendContext ctx)
    {
        if (ctx.Gate is not { } gate)
        {
            return null;
        }

        return await gate
            .AcquireAsync(ctx.Config, request, ctx.ExternalToken, ctx.Config.LimitsOrNull?.QueueTimeout)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 阶段 3 —— 取连接：注入 Handler 时固定用它的 <see cref="HttpClient"/>；默认实例按 origin 租一条
    /// （连接复用 + 每 origin 并发上限）。租用也走异步入口，已取消 / 已超时的请求在租用前就被截断，
    /// 异常同样翻译成统一错误模型（见 <see cref="AttemptBudget.Translate"/>）。
    /// <para>
    /// 「这次是不是顺手新建了 origin 节点」写进 span：首个请求要付 Handler 构造 + TLS 握手，
    /// 时长毛刺里有一类原因就是它。自定义 Handler 注入时没有池，这条标签自然缺席。
    /// 返回 <see cref="ValueTask{TResult}"/>：两种取法都不会真的挂起，用 Task 会让每个请求白建一个 Task。
    /// </para>
    /// </summary>
    private async ValueTask<(HttpClient Client, PoolLease? Lease)> LeaseClientAsync(
        HttpRequestMessage request,
        SendContext ctx,
        AttemptBudget budget)
    {
        // 注入 Handler：固定 HttpClient，没有租约要归还。
        if (_customClient is { } custom)
        {
            return (custom, null);
        }

        PoolLease lease;
        try
        {
            lease = await _pool!.LeaseAsync(ctx.Uri, ctx.Config, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw budget.Translate(ex, ctx.Config, request);
        }

        AxiosTelemetry.TagPoolReuse(lease.EndpointReused);
        return (lease.Client, lease);
    }

    /// <summary>
    /// 阶段 4 —— 发送：只到响应头（<see cref="HttpCompletionOption.ResponseHeadersRead"/>），正文留给读取阶段
    /// （也让库 span 能单独量出正文下载时间）。异常在这里统一翻译成错误模型：取消 / 超时、网络、
    /// 以及被 <see cref="HttpRequestException"/> 包住的库异常 —— 后者是「正文序列化 / 上传进度回调写错」的形态，
    /// 原样还原才不会误报成 ERR_NETWORK。
    /// </summary>
    private static async Task<HttpResponseMessage> SendRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        SendContext ctx,
        AttemptBudget budget)
    {
        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw budget.Translate(ex, ctx.Config, request);
        }
        catch (HttpRequestException ex) when (FindAxiosException(ex) is { } translated)
        {
            // 正文序列化（含上传进度回调）抛出的库异常会被 HttpClient 包成
            // HttpRequestException：原样还原，否则「回调写错」会被误报成 ERR_NETWORK。
            ExceptionDispatchInfo.Capture(translated).Throw();
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (IOException ex)
        {
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (NotSupportedException ex)
        {
            // 自定义 Handler / Content 不支持某种能力时不要把裸异常漏给调用方。
            throw AxiosException.Network(ctx.Config, request, ex);
        }
    }

    /// <summary>
    /// 阶段 5 —— 读取：先判定状态码接受性，再把正文读成 <see cref="AxiosResponse"/>。
    /// 接受性只判定一次（它同时决定宽松解析与是否抛错），判两次会让有副作用的 validateStatus 执行两遍；
    /// 不合法时宽松解析，否则 404 + HTML 会在反序列化处抛错，把本该带 Status/Response 的 ERR_BAD_REQUEST
    /// 变成 ERR_BAD_RESPONSE 且两者全丢。任何失败路径都先释放响应消息（成功路径见阶段 6）。
    /// </summary>
    private static async Task<(AxiosResponse Response, bool StatusAccepted)> ReadResponseAsync(
        HttpResponseMessage responseMessage,
        HttpRequestMessage request,
        SendContext ctx,
        AttemptBudget budget)
    {
        try
        {
            var validateStatus = ctx.Config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
            var statusAccepted = validateStatus((int)responseMessage.StatusCode);

            var response = await ResponseReader.ReadAsync(
                    responseMessage,
                    request,
                    ctx.Config,
                    budget.Token,
                    ctx.DataType,
                    ctx.Target,
                    lenient: !statusAccepted)
                .ConfigureAwait(false);

            return (response, statusAccepted);
        }
        catch (OperationCanceledException ex)
        {
            responseMessage.Dispose();
            throw budget.Translate(ex, ctx.Config, request);
        }
        catch (IOException ex)
        {
            // 连接中途断开 / 正文被截断（HttpIOException）：统一成 ERR_NETWORK。
            responseMessage.Dispose();
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (HttpRequestException ex)
        {
            // 自定义 Handler 的正文流可能直接抛 HttpRequestException（BCL 抛 HttpIOException）：
            // 与上面的 IO 失败同一错误码，不能让裸异常穿过统一错误模型。
            responseMessage.Dispose();
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch
        {
            // 读取/解析失败（含长度超限、反序列化失败）：先释放响应，避免连接泄漏
            responseMessage.Dispose();
            throw;
        }
    }

    private static bool DefaultValidateStatus(int status) => status is >= 200 and < 300;

    /// <summary>
    /// 沿 <see cref="Exception.InnerException"/> 链找库自己的异常（HttpClient 会把正文序列化异常
    /// 包成 <see cref="HttpRequestException"/>，需要还原成原来的错误码）。
    /// </summary>
    private static AxiosException? FindAxiosException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AxiosException axios)
            {
                return axios;
            }
        }

        return null;
    }

    /// <summary>
    /// 响应拦截器返回 <c>null</c>：返回类型非空，静默交出去只会让调用方在很远的地方炸 NRE。
    /// </summary>
    private static AxiosException NullInterceptorResult(AxiosConfig config)
        => AxiosException.InvalidOption(
            "响应拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosResponse 实例"
            + "（要中断这次请求请抛异常，不要返回 null）。",
            config);

    /// <summary>流式失败响应保留的错误体上限：错误详情通常很小，超过这个量说明不是给人看的信息。</summary>
    private const int StreamErrorBodyLimit = 64 * 1024;

    /// <summary>
    /// 把流式失败响应的正文读成文本（最多 <see cref="StreamErrorBodyLimit"/> 字节）。不读的话调用方只能看到
    /// 一句 "status code 400"；读取失败 / 超时 / 超限都只保留已读部分，不掩盖原始 HTTP 错误。
    /// </summary>
    private static async Task<string?> ReadStreamErrorBodyAsync(
        Stream? body,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return null;
        }

        var limit = config.LimitsOrNull?.MaxContentLength is long max && max < StreamErrorBodyLimit
            ? (int)max
            : StreamErrorBodyLimit;
        if (limit <= 0)
        {
            return null;
        }

        // 失败路径的一次性缓冲走 ArrayPool，大错误体 + 高频重试时不再每请求一块 64KB。
        // Rent 可能返回比 limit 大的缓冲，读取上限仍按 limit 算（抢救量有契约上限）。
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        var total = 0;
        try
        {
            try
            {
                while (total < limit)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or AxiosException or OperationCanceledException)
            {
                // 错误体读不完不影响「这是一次失败响应」，保留已读部分
            }

            // 解码必须在 finally 归还缓冲之前完成：归还后该数组可能立刻被别的请求租走并覆写。
            return total == 0
                ? null
                : ResponseTextDecoder.Decode(buffer.AsSpan(0, total), config.Response?.CharSet, config.SerializationOrNull?.ResponseEncoding);
        }
        finally
        {
            // 错误体可能含敏感信息：清空后再归还，避免下一个租用方读到上一请求的正文。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// 「明确要求 HTTP/3」时的前置校验：环境上不可能做到就立刻报配置错误，而不是让调用方拿一次
    /// 莫名其妙的连接超时去猜原因。只拦 <c>RequestVersionExact</c> / <c>RequestVersionOrHigher</c>
    /// （语义是「必须至少 3.0」，做不到就没有任何成功可能）；<c>RequestVersionOrLower</c> 的契约本就是
    /// 「能 h3 就 h3、不能就降级」，降级是正常路径（真实版本看 <see cref="AxiosResponse.ProtocolVersion"/>），
    /// 拦下来反而破坏默认语义。判据用本地能力 <see cref="Http3Support"/> 而非「服务端支不支持」——
    /// 后者只能发请求才知道，那种失败仍走 <c>ERR_NETWORK</c>。
    /// </summary>
    private static void ValidateHttp3Request(Uri uri, Version version, HttpVersionPolicy policy, AxiosConfig config)
    {
        if (version.Major < 3 || policy == HttpVersionPolicy.RequestVersionOrLower)
        {
            return;
        }

        // 明文目标上不存在 h3（QUIC 自带 TLS 1.3，没有「明文 h3」这种东西）：这类配置错误与运行时不支持
        // 混在一起报，会让人去查 MsQuic 而真凶其实是 http://。
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw AxiosException.InvalidOption(
                $"HTTP/3 只能跑在 QUIC（TLS 1.3 over UDP）上，{uri.Scheme}:// 目标不存在 h3："
                + "请改用 https 目标；要在明文目标上自动落到 1.1，请把 VersionPolicy 设为 RequestVersionOrLower。",
                config);
        }

        if (!Http3Support.IsSupported)
        {
            throw AxiosException.InvalidOption(
                $"当前环境跑不了 HTTP/3，无法满足 HttpVersion = 3.0 + VersionPolicy = {policy}："
                + $"{Http3Support.UnsupportedReason}。"
                + "想要「有 h3 就用 h3、没有就自动降级」，请把 VersionPolicy 设为 RequestVersionOrLower。",
                config);
        }
    }

    /// <summary>
    /// 限制类配置的取值范围校验：负值一律报错，不静默当作「不限制」。
    /// <c>Timeout=0</c> 不超时；<c>MaxRate=0</c> 等价不限速；<c>null</c> 不限。
    /// </summary>
    private static void ValidateRangeConfig(AxiosConfig config)
    {
        if (config.Timeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption(
                $"Timeout 不能为负（当前 {config.Timeout}）：要关闭超时请显式设为 TimeSpan.Zero。", config);
        }

        var limits = config.LimitsOrNull;

        if (limits?.StreamIdleTimeout is { } idleTimeout && idleTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption(
                $"StreamIdleTimeout 不能为负（当前 {idleTimeout}）：要不设空闲超时请显式设为 TimeSpan.Zero。", config);
        }

        if (limits?.MaxRate is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxRate 不能为负（当前 {limits.MaxRate}）：要不限速请设为 null（0 等价于不限速）。", config);
        }

        if (limits?.MaxContentLength is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxContentLength 不能为负（当前 {limits.MaxContentLength}）：不限制请设为 null。", config);
        }

        if (limits?.MaxBodyLength is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxBodyLength 不能为负（当前 {limits.MaxBodyLength}）：不限制请设为 null。", config);
        }

        // 并发上限：0 与负数都没有语义（「同时最多 0 条请求」等于永久失败），
        // 静默当成「不限制」会让调用方以为限流生效了 —— 与 MaxRate / MaxContentLength 同一条线。
        if (limits?.MaxConcurrentRequests is int maxConcurrency && maxConcurrency < 1)
        {
            throw AxiosException.InvalidOption(
                $"MaxConcurrentRequests 必须是正整数（当前 {maxConcurrency}）：不限制请设为 null，"
                + "要「不排队」请把 QueueTimeout 设为 TimeSpan.Zero。", config);
        }

        if (limits?.QueueTimeout is { } queueTimeout && queueTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption(
                $"QueueTimeout 不能为负（当前 {queueTimeout}）：要不排队请设为 TimeSpan.Zero，要一直等请设为 null。", config);
        }

        // 分帧上限：既不能「设 0 当不限制」（分帧器没有关闭语义），也不能大到溢出 int —— 越界在发送前就报，
        // 而不是等第一次大帧在分帧器里炸成裸异常。三个边界值都在 LimitSettings 上，这里只负责比对。
        if (limits?.MaxStreamFrameLength is int frameLength
            && (frameLength < AxiosConfig.LimitSettings.MinStreamFrameLength
                || frameLength > AxiosConfig.LimitSettings.MaxStreamFrameLengthLimit))
        {
            throw AxiosException.InvalidOption(
                $"MaxStreamFrameLength 超出范围（当前 {frameLength} 字节）：合法区间 "
                + $"{AxiosConfig.LimitSettings.MinStreamFrameLength}–{AxiosConfig.LimitSettings.MaxStreamFrameLengthLimit} 字节（1–64 MB），"
                + $"默认 {AxiosConfig.LimitSettings.DefaultStreamFrameLength} 字节（8 MB）。"
                + "分帧器必须把整帧读进内存才能交给序列化器，因此没有「不限制」语义；更大的正文请用 ResponseType.Stream 自行分块处理。",
                config);
        }

        // 重试策略的取值范围：次数为负没有语义，等待时间为负同样无意义。自定义策略只能校验接口上的
        // MaxRetries；内置实现连等待时间一起校验。
        if (config.RetryOrNull?.Policy is { } retry)
        {
            if (retry.MaxRetries < 0)
            {
                throw AxiosException.InvalidOption(
                    $"重试次数 MaxRetries 不能为负（当前 {retry.MaxRetries}）：不重试请设为 0 或不要设置 Retry.Policy。",
                    config);
            }

            if (retry is RetryPolicy policy)
            {
                if (policy.Delay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption(
                        $"重试间隔 Delay 不能为负（当前 {policy.Delay}）：要立即重试请设为 TimeSpan.Zero。", config);
                }

                if (policy.MaxDelay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption(
                        $"重试间隔上限 MaxDelay 不能为负（当前 {policy.MaxDelay}）：不封顶请设为一个较大的值（上限约 49.7 天）。",
                        config);
                }

                // 等待走 Task.Delay，与 CancelAfter 同一个计时器队列（上限约 49.7 天）。这里在发请求之前就
                // 明确报错而不是像 Timeout 那样静默收敛：MaxDelay 是显式配置的上限，截断会让「最多等这么久」失效。
                if (policy.MaxDelay > Timeouts.Max)
                {
                    throw AxiosException.InvalidOption(
                        $"重试间隔上限 MaxDelay 超出计时器可表示的范围（当前 {policy.MaxDelay}，上限约 49.7 天）："
                        + "重试等待用 Task.Delay 实现，更长的间隔无法表达。请调小 MaxDelay，"
                        + "长时间退避请减少次数并用 CancellationToken 控制退出。",
                        config);
                }
            }
        }

        // 熔断策略的取值范围：「连续 0 次失败就熔断」「放行 0 个探测」「冷却 0 秒」都没有可执行语义，
        // 静默取默认值只会让调用方以为配置生效了。与重试同理，只校验内置实现暴露的项。
        if (config.CircuitOrNull?.Policy is CircuitBreaker breaker)
        {
            if (breaker.FailureThreshold < 1)
            {
                throw AxiosException.InvalidOption(
                    $"熔断阈值 FailureThreshold 必须 >= 1（当前 {breaker.FailureThreshold}）："
                    + "它表示「连续多少次下游故障后打开熔断」，不熔断请把 Circuit.Policy 设为 null。",
                    config);
            }

            if (breaker.HalfOpenAttempts < 1)
            {
                throw AxiosException.InvalidOption(
                    $"半开探测名额 HalfOpenAttempts 必须 >= 1（当前 {breaker.HalfOpenAttempts}）："
                    + "一个探测都不放行的熔断器永远无法恢复。",
                    config);
            }

            if (breaker.MaxTrackedHosts < 1)
            {
                throw AxiosException.InvalidOption(
                    $"熔断跟踪维度上限 MaxTrackedHosts 必须 >= 1（当前 {breaker.MaxTrackedHosts}）："
                    + "上限为 0 会让每个请求都新建一条状态、记不住任何失败。",
                    config);
            }

            if (breaker.BreakDuration <= TimeSpan.Zero)
            {
                throw AxiosException.InvalidOption(
                    $"熔断冷却时长 BreakDuration 必须 > 0（当前 {breaker.BreakDuration}）："
                    + "零冷却等价于「一打开就立刻探测」，熔断形同虚设；不熔断请把 Circuit.Policy 设为 null。",
                    config);
            }
        }
    }

    /// <summary>
    /// 解析（必要时懒创建）本实例的并发闸门。闸门是实例级资源：信号量容量创建时固定、之后无法重建
    /// （重建会让已在排队的请求永远等一个不再被归还的名额），所以「本次生效上限 ≠ 闸门容量」一律报错，
    /// 而不是静默按旧上限跑 —— 设了上限却按别的上限执行，比直接失败更难查。两种来源都会走到这里：
    /// 按请求写了不同的值、以及构造之后改了 <c>Defaults</c>。
    /// <para>
    /// 未设置（<c>null</c>）时不建闸门；实例已有闸门时按闸门走（<c>null</c> 视为「本次没表态」，
    /// 因为「本次不限制」在共享信号量的模型下无法表达）。
    /// </para>
    /// </summary>
    private ConcurrencyGate? ResolveConcurrencyGate(AxiosConfig config)
    {
        var requested = config.LimitsOrNull?.MaxConcurrentRequests;
        var existing = Volatile.Read(ref _gate);

        if (existing is not null)
        {
            if (requested is int value && value != existing.MaxConcurrency)
            {
                throw ConcurrencyMismatch(config, value, existing.MaxConcurrency);
            }

            return existing;
        }

        if (requested is not int limit)
        {
            return null;
        }

        var created = new ConcurrencyGate(limit);

        // 并发首请求：输的一方用赢家的闸门，容量不同则报错（两个线程带着不同上限同时首次请求）
        var winner = Interlocked.CompareExchange(ref _gate, created, null);
        if (winner is null)
        {
            return created;
        }

        if (winner.MaxConcurrency != limit)
        {
            throw ConcurrencyMismatch(config, limit, winner.MaxConcurrency);
        }

        return winner;
    }

    private static AxiosException ConcurrencyMismatch(AxiosConfig config, int requested, int effective)
    {
        return AxiosException.InvalidOption(
            $"MaxConcurrentRequests 是实例级配置（并发闸门在首个受限请求时创建并固定为 {effective}），"
            + $"本次生效配置要求 {requested}。按请求写不同的值不会生效，构造之后改 Defaults 也不会重建闸门；"
            + "要改变上限请新建 AxiosClient 并在 defaults 上设置。",
            config);
    }

    /// <summary>
    /// 实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：
    /// 按请求复述实例的等价设置放行，写入别的值报错。
    /// <para>
    /// 等价判定分两类：<c>WithCredentials</c> / <c>MaxRedirects</c> 是值类型，按<b>值</b>比较；
    /// Proxy / Credentials / PoolOptions 没有可靠的等值定义（「相同」取决于各自实现），按<b>引用</b>比较 ——
    /// 只有「把实例 defaults 上的同一个对象再写一遍」才放行。判定对象是合并后的生效配置，不必追是谁写的。
    /// </para>
    /// </summary>
    private void ValidatePerRequestOptions(AxiosConfig config)
    {
        var requested = config.ConnectionOrNull;
        var defaultsConnection = Defaults.ConnectionOrNull;

        if (requested?.Proxy is not null && !ReferenceEquals(requested.Proxy, defaultsConnection?.Proxy))
        {
            throw AxiosException.InvalidOption(
                "Proxy 是实例级配置（连接池在构造 AxiosClient 时创建），请改用 new AxiosClient(new AxiosConfig { Connection = new() { Proxy = ... } }) "
                + "或 ConnectionPoolOptions.Proxy。", config);
        }

        if (requested?.Credentials is not null && !ReferenceEquals(requested.Credentials, defaultsConnection?.Credentials))
        {
            throw AxiosException.InvalidOption(
                "Credentials 是实例级配置，请在 AxiosClient 的 defaults 或 ConnectionPoolOptions 上设置。", config);
        }

        // 池在构造时已经建好，按请求改它只会被静默丢弃 —— 与 Proxy/MaxRedirects 保持一致地报错。
        if (requested?.PoolOptions is not null && !ReferenceEquals(requested.PoolOptions, defaultsConnection?.PoolOptions))
        {
            throw AxiosException.InvalidOption(
                "PoolOptions 是实例级配置（连接池在构造 AxiosClient 时创建），"
                + "请通过 AxiosClient 构造参数或 defaults.PoolOptions 设置。", config);
        }

        ValidateWithCredentials(config);
        ValidateMaxRedirects(config);
    }

    /// <summary>
    /// <c>WithCredentials</c> 只允许复述实例生效值（值相同即等价）；注入自定义 Handler 时无从判断是否等价，
    /// 只要写就报错。
    /// <para>
    /// 「实例生效值」取池的 <see cref="ConnectionPoolOptions.UseCookies"/>，与 <see cref="ValidateMaxRedirects"/>
    /// 取池的 <c>AllowAutoRedirect</c> 同一口径 —— 不能读 <c>Defaults.WithCredentials</c>：连接级选项构造时
    /// 已固化进池，构造后再改 <c>Defaults</c> 静默不生效，拿它当生效值会让校验结论与线上行为相反。
    /// 共享池同理：Cookie 容器由池决定，与本实例 defaults 无关。
    /// </para>
    /// <para>
    /// 必须先按 <see cref="AxiosConfig.WithCredentialsSet"/> 早退：合并产物会清掉继承来的标记
    /// （见 <c>MergeConfig</c> 的 <c>ClearInstanceOptionFlags</c>），此处的标记只代表「按请求显式写过」。
    /// 否则「构造后改过 Defaults」的实例会在<b>每个</b>普通请求上被判成写入了不同的值。
    /// </para>
    /// </summary>
    private void ValidateWithCredentials(AxiosConfig config)
    {
        if (_customClient is not null)
        {
            if (config.WithCredentialsSet)
            {
                throw AxiosException.InvalidOption(
                    "注入自定义 handler 时 WithCredentials 完全由 handler 决定（Cookie 容器在连接层），"
                    + "本库无法判断按请求写的值与实际行为是否等价，请在 handler 上配置。", config);
            }

            return;
        }

        // 本次没写这一项（含快捷方法从 Defaults.Clone() 继承的未设置态）直接放行：
        // 池已按构造期设置固化，按请求没表态就没有可校验的东西。
        if (!config.WithCredentialsSet)
        {
            return;
        }

        var requestedValue = config.ConnectionOrNull?.WithCredentials ?? false;
        var effective = _pool!.EffectiveOptions.UseCookies;

        if (requestedValue == effective)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"WithCredentials 是实例级配置（影响 Cookie 容器与系统凭据），按请求只能写与实例相同的值；"
            + $"当前按请求为 {requestedValue}，实例生效值为 {effective}。"
            + "要改变它请新建 AxiosClient 并在 defaults 上设置。", config);
    }

    /// <summary>
    /// <c>MaxRedirects</c> 只允许写与实例生效值相同的值；生效值取池的实际策略
    /// （<c>AllowAutoRedirect = false</c> 时视为 0），不是属性默认值 5。
    /// </summary>
    private void ValidateMaxRedirects(AxiosConfig config)
    {
        if (_customClient is not null)
        {
            if (config.MaxRedirectsSet)
            {
                throw AxiosException.InvalidOption(
                    "注入自定义 handler 时 MaxRedirects 由 handler 决定（重定向在连接层处理），"
                    + "本库无法判断按请求写的值与实际行为是否等价，请在 handler 上配置。", config);
            }

            return;
        }

        // 未显式写过（含快捷方法从 Defaults.Clone() 继承的未设置态）直接放行。不能拿属性默认值 5 做相等短路：
        // poolOptions 配了别的重定向策略时它与池生效值背离，按请求显式写的值会被静默放行（文档承诺抛错）。
        if (!config.MaxRedirectsSet)
        {
            return;
        }

        var options = _pool!.EffectiveOptions;
        var effective = options.AllowAutoRedirect ? options.MaxAutomaticRedirections : 0;

        var requestedMaxRedirects = config.ConnectionOrNull?.MaxRedirects ?? 5;

        if (requestedMaxRedirects == effective)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"MaxRedirects 是实例级配置（重定向由连接池 Handler 处理），按请求只能写与实例相同的值；"
            + $"当前按请求为 {requestedMaxRedirects}，实例生效值为 {effective}（0 表示不跟随重定向）。"
            + "要改变它请新建 AxiosClient 并在 defaults 或 ConnectionPoolOptions 上设置。", config);
    }

    /// <summary>
    /// 释放实例：<b>不打断在途请求</b>——资源释放推迟到最后一个请求结束，
    /// 此后不再接受新请求（抛 <c>ERR_CANCELED</c>）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _inFlight) > 0)
        {
            // 在途请求结束时由 ReleaseInFlight 补做释放
            return;
        }

        DisposeResources();
    }

    /// <summary>资源释放本体（幂等）：注入的 HttpClient/Handler 与本实例自建的池。</summary>
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _customClient?.Dispose();

        if (_ownsCustomHandler)
        {
            _customHandler?.Dispose();
        }

        if (_ownsPool)
        {
            // 优雅关闭：空闲节点立即释放，在途请求的最后一条租约归还时才回收节点。
            _pool?.Dispose();
        }
    }

    /// <summary>
    /// 与 <see cref="Dispose"/> 等价；连接池关闭本身是同步的，实现它只为支持 <c>await using</c>。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
