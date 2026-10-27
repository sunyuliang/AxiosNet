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

    /// <summary>在途并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）：首个受限请求时按当时上限创建并固定，之后改上限会抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    private ConcurrencyGate? _gate;

    /// <summary>
    /// 对应 axios instance.defaults。连接级选项（WithCredentials / MaxRedirects / Proxy / Credentials / PoolOptions）
    /// 构造时已固化，改后不生效；MaxConcurrentRequests 的闸门在首个受限请求时创建，此后改会抛 <c>ERR_BAD_OPTION_VALUE</c>。
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
    /// </param>
    /// <param name="poolOptions">本实例自建连接池的选项。</param>
    /// <param name="handler">
    /// 直接注入的 Handler；注入后不创建连接池，连接级选项由它自己决定。
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

            // 池的连接行为已固定，defaults 上的同类选项无从生效，故明确报错。
            ValidateConnectionLevelOptions(defaults, Defaults, ConnectionLevelMessages.SharedPool);

            _pool = pool;
            _ownsPool = false;
        }
        else
        {
            // 两处都配时明确报错，不静默取一个。
            if (poolOptions is not null && Defaults.ConnectionOrNull?.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 defaults.PoolOptions：两者描述的是同一个自建连接池，请只在一处配置。", Defaults);
            }

            var effective = (poolOptions ?? Defaults.ConnectionOrNull?.PoolOptions ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期带配置抛错。
            HttpConnectionPool.ValidateOptions(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向，> 0 = 覆盖池默认上限；必须按 *Set 判断，
            // 否则会把调用方在 poolOptions 上配好的 MaxAutomaticRedirections 改回 5。
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

            // 两处都写时明确报错，不静默取一个。
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

    /// <summary>连接级选项无法按 defaults 生效时的构造期校验（注入 handler / 共享池共用判定，只是文案不同）。</summary>
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

    // ---- getUri：只组装 URL，不发请求（对应 axios.getUri） ----

    /// <summary>按本实例的生效配置拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求；返回已转义，可直接用于签名与日志。传入生效配置（如 <see cref="AxiosResponse.Config"/>）时按回放语义重新合并，实例级项取自本实例。</summary>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 回放 response.Config 时实例级项让位给本实例默认值，与 RequestAsync 实际发出的地址一致。
        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.SerializationOrNull?.ParamsSerializer, config: merged).AbsoluteUri;
    }

    /// <summary>按 Action 配置拼出最终请求 URL，不发请求；回调在实例默认值的副本上修改（与快捷方法同一模型）。</summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // 直接拼生效配置，不走 MergeConfig：它的 instanceSourced 门禁会丢掉回调里显式写的 BaseAddress。
        // 请求头不参与 URL 拼装，因此不需要 ApplyRemoveHeaders。
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

        // 回调在实例默认值的副本上增删改，删头 / 清空头集合都真的生效（见 NewEffectiveConfig）。
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

    // ---- 核心请求管线 ----

    private const string DisposedMessage = "AxiosClient 已释放，不能发起新请求";

    /// <summary>最后一个在途请求结束时，补做 <see cref="Dispose"/> 推迟的资源释放。</summary>
    private void ReleaseInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) == 1)
        {
            DisposeResources();
        }
    }

    /// <summary>阶段 0 —— 入口守卫：先占一个在途名额，再复核释放标记；必须在调用方的 <c>try</c> 之外（它抛时名额还没占上，不该归还）。</summary>
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

    /// <summary>请求入口：按固定顺序驱动整条管线 —— 合并 → 校验（实例级）→ 拦截 → 规范化 →（短路 | 校验 + 闸门 → 编码 → 发送）→ 收尾；顺序是契约，理由见各阶段方法。</summary>
    private async Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // 阶段 0：守卫。放在 try 之外：它抛时名额还没占上，finally 不该归还。
        EnterInFlight(perCallConfig);

        // 遥测状态全是局部量：关闭遥测时零分配；后三项在 URL 拼好后才定。
        AxiosConfig? config = null;
        Uri? telemetryUri = null;
        var telemetryEnabled = false;
        var telemetryStart = 0L;
        Activity? telemetryActivity = null;

        try
        {
            // 阶段 1：合并 —— 得到本次请求的生效配置。
            config = MergeEffectiveConfig(perCallConfig);

            // 阶段 2：校验（实例级选项）—— 构造期已固化进连接池，改不了，故判在拦截器之前。
            ValidatePerRequestOptions(config);

            // 阶段 3：拦截 —— 请求拦截器按注册逆序执行（与 axios 一致）。
            config = await InterceptRequestAsync(config).ConfigureAwait(false);

            // 阶段 4a ~ 4d 都必须看拦截器之后的配置，否则拦截器可以绕过护栏或修正非法值。
            ApplyAutoResponseType(config, dataType);

            // 范围校验判的是配置自身是否可执行，与会不会真的发出请求无关，故放在短路之前。
            ValidateRangeConfig(config);

            // 闸门与编码只对真实 HTTP 操作有意义，短路不经过它们。
            if (config.ShortCircuit is { } shortCircuit)
            {
                return await CompleteShortCircuitAsync(shortCircuit, config, dataType, target).ConfigureAwait(false);
            }

            var gate = ResolveConcurrencyGate(config);

            // 阶段 5：编码 —— transformRequest → URL / 协议 → 正文 / 请求头。
            ApplyRequestTransforms(config);
            var plan = BuildRequestPlan(config);

            // 遥测起点必须在 URL 拼好之后，且只数真的进入发送阶段的请求。
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
                var (result, streamOwner) = await RunResponseInterceptorsAsync(template, SendContext.From(config, plan.Uri, dataType, target, gate)).ConfigureAwait(false);

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

    /// <summary>阶段 1 —— 合并：把按请求配置落到实例默认值上；已是合并产物则直接用。</summary>
    private AxiosConfig MergeEffectiveConfig(AxiosConfig perCallConfig)
        => perCallConfig.EffectiveMerged ? perCallConfig : MergeConfig(perCallConfig);

    /// <summary>阶段 3 —— 拦截：请求拦截器按注册逆序执行（与 axios 一致），返回最终生效配置；返回 null Task / null 配置都就地报错。</summary>
    private async ValueTask<AxiosConfig> InterceptRequestAsync(AxiosConfig config)
    {
        foreach (var interceptor in Interceptors.Request.SnapshotReversed())
        {
            AxiosConfig next;
            try
            {
                // 返回 null Task 会炸成裸 NRE，这里提前给出可定位的错误（同步结果请用 Task.FromResult）。
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

                var rejected = interceptor.OnRejected(ex)
                    ?? throw AxiosException.InvalidOption(
                        "请求拦截器的 OnRejected 返回了 null Task：必须返回 Task<AxiosConfig> 实例"
                        + "（同步结果请用 Task.FromResult 包装）。",
                        config);
                next = await rejected.ConfigureAwait(false);
            }

            // 返回 null 会在下一层炸成裸 NRE，调用方看不出是哪层拦截器出的错。
            config = next ?? throw AxiosException.InvalidOption(
                "请求拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosConfig 实例"
                + "（要做修改请改传入的 config 后端返回它）。",
                config);
        }

        return config;
    }

    /// <summary>
    /// 阶段 4a —— 规范化：泛型 <c>&lt;dynamic&gt;</c> / <c>&lt;object&gt;</c> 与 <c>&lt;Stream&gt;</c> 自动切换响应形态。
    /// 判据是 <see cref="AxiosConfig.ResponseTypeSet"/>（Json 是默认值，只看值分不清「没表态」），切换本身不置该标记。
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

    /// <summary>阶段 5a —— 编码：<c>transformRequest</c> 依次作用于数据（拦截器之后、序列化之前）。</summary>
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

    /// <summary>一次请求的「成型结果」：最终 URL 与协议协商参数，结构体以免热路径多一次堆分配。</summary>
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
    /// 默认 HTTP/2 + <see cref="HttpVersionPolicy.RequestVersionOrLower"/>（不支持就降级是库的契约）；
    /// 明文 http:// 一律 1.1；强制 1.1 写 Version11，要 h3 写 Version30。
    /// </summary>
    private static RequestPlan BuildRequestPlan(AxiosConfig config)
    {
        var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.ParamsSerializer, config: config);

        var connection = config.ConnectionOrNull;
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version20;
        var versionPolicy = connection?.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower;

        // HTTP/3 的前置校验：只在真的要 h3 时走（h2 / 1.1 请求一次 Major 比较就返回）。
        ValidateHttp3Request(uri, httpVersion, versionPolicy, config);

        return new RequestPlan(uri, httpVersion, versionPolicy);
    }

    /// <summary>阶段 5c —— 编码：建请求模板（正文 + 请求头）。正文只准备一次，重试复用（进度与限速状态都挂在正文上）；成功返回时模板所有权交给调用方，内部失败自己释放。</summary>
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
                // 序列化失败走统一错误模型，取消类异常原样抛出；声明式表单报模型名而不是 Dictionary`2。
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

    /// <summary>阶段 6 —— 发送：重试 / 熔断的发送循环，外面按注册顺序套上响应拦截器责任链。重试在链内，拦截器只看到最终结果；返回的 <c>StreamOwner</c> 是流式正文的所有权凭证。</summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> RunResponseInterceptorsAsync(
        HttpRequestMessage template,
        SendContext ctx)
    {
        var responseInterceptors = Interceptors.Response.Snapshot();

        if (responseInterceptors.Length == 0)
        {
            // 空链：直接交回发送的 Task，无需兜底释放。
            return SendWithRetryAsync(template, ctx);
        }

        return RunResponseChainAsync(template, ctx, responseInterceptors);
    }

    /// <summary>响应拦截器责任链（只在有拦截器时走到）：按注册顺序包装，最内层是真正的发送；任何一层抛异常都要归还流式正文的租约。</summary>
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

    /// <summary>把拦截器按注册顺序套在末端调用外面，最内层是「真正产生响应的那一步」；网络路径与短路路径共用。返回 <c>null</c> 就地报错。</summary>
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

                    // 返回 null 会让调用方随后炸 NRE，明确报错。
                    return (await rejected(ex).ConfigureAwait(false))
                        ?? throw NullInterceptorResult(config);
                }

                return (await fulfilled(response).ConfigureAwait(false))
                    ?? throw NullInterceptorResult(config);
            };
        }

        return chain;
    }

    /// <summary>阶段 4c —— 短路：交付调用方给的响应，本次请求不发送。反序列化、transformResponse、validateStatus 与响应拦截器照常执行，只跳过并发闸门、编码、租约、发送、重试、熔断与遥测。</summary>
    private async Task<AxiosResponse> CompleteShortCircuitAsync(
        AxiosShortCircuit reply,
        AxiosConfig config,
        Type? dataType,
        AxiosResponse? target)
    {
        // 没有 I/O 也尊重取消意图。
        if (config.CancellationToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(config, null);
        }

        var validateStatus = config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
        var statusAccepted = validateStatus(reply.Status);

        // 状态码不合法时按宽松口径解析，让 404 + HTML 也能带着 Status/Response 抛错。
        var response = ResponseReader.ReadShortCircuit(reply, config, dataType, target, lenient: !statusAccepted);

        // 末端：不合法状态码在这里抛，放在链内才能被 OnRejected 接住。
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
            // 空链：直接交付。
            return await Terminal().ConfigureAwait(false);
        }

        var chain = WrapWithInterceptors(Terminal, responseInterceptors, config);
        return await chain().ConfigureAwait(false);
    }

    /// <summary>一次请求在「发送阶段」不变的输入，打成结构体收敛发送方法的入参；不含 <see cref="HttpRequestMessage"/>（它逐次尝试变，见 <see cref="CloneRequest"/>）。</summary>
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

        /// <summary>唯一创建入口：派生项（令牌 / 流式标记 / 重试与熔断配置 / 正文可重放性）在这里一次算清。</summary>
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

                // 来自流的正文必须能拨回起点，否则重试只会发出空内容；在发请求之前就拦下。
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

    /// <summary>带重试的发送入口：没有策略时直接交回单次发送的 Task。</summary>
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

    /// <summary>重试循环（只在配置了策略时走到）：每次尝试换一条新请求，正文与请求头沿用模板；每次尝试有独立的超时预算（<see cref="AxiosConfig.Timeout"/> 是单次尝试上限），调用方令牌贯穿所有尝试与等待。</summary>
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
                // 同一份正文再发一次：先把读指针拨回起点。放在 try 之外：拨不动说明正文不可重放，
                // 不该再拿去问策略要不要重试。
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
                // 取消类异常直接冒到调用方，不问策略是否重试。
                var failed = new AxiosRetryContext(attempt, policy.MaxRetries, ex, ctx.Config, (ex as AxiosException)?.Response);

                if (!CanRetry(failed, externalToken, policy))
                {
                    throw;
                }

                var delay = policy.GetDelay(failed);
                if (delay < TimeSpan.Zero)
                {
                    // 策略算错时间不该把请求变成配置错误：负值按「不等待」处理。
                    delay = TimeSpan.Zero;
                }
                else if (delay > Timeouts.Max)
                {
                    // Task.Delay 上限约 49.7 天，自定义策略可返回任意值，在这里收敛。
                    delay = Timeouts.Max;
                }

                // 回调与遥测共用同一个上下文
                var retryContext = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, ctx.Config, failed.Response, delay);

                // 回调在 catch 块里调用：它抛出的异常按约定原样抛给调用方，不再问策略。
                retry.OnRetry?.Invoke(retryContext);

                // 观测点放在「决定重试」这一刻，与 OnRetry 同一语义。
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

    /// <summary>一次尝试的入口：先过熔断闸门，再发送；没有熔断策略时直接转给 <see cref="SendCoreAsync"/>。</summary>
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

    /// <summary>带熔断的单次尝试：放行 → 发送 → 回报结果。拒绝时直接抛 <c>ERR_CIRCUIT_OPEN</c>；回报必须恰好一次，漏掉「取消」会让熔断器卡在半开。</summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithCircuitAsync(
        HttpRequestMessage request,
        SendContext ctx,
        IAxiosCircuitPolicy policy)
    {
        // 调用方已取消就先报取消，否则熔断拒绝会盖掉真正的退出原因。
        if (ctx.ExternalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(ctx.Config, request);
        }

        var context = new AxiosCircuitContext(CircuitHost(ctx.Uri), ctx.Uri, ctx.Config);

        if (!policy.ShouldAllow(context))
        {
            // 建议等待时间：策略说不准时给零，错误消息里就不带「retry after」。
            var retryAfter = policy.GetRetryAfter(context);

            // 回调在抛错之前调用，其异常按约定原样抛出；上下文带上建议等待时间。
            ctx.Circuit!.OnRejected?.Invoke(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, retryAfter: retryAfter));

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
            // 失败结论带上异常，策略据此区分下游故障与取消 / 4xx；策略异常按约定原样抛出。
            policy.Record(new AxiosCircuitContext(context.Host, ctx.Uri, ctx.Config, ex));
            throw;
        }

        // 状态码一并回报（validateStatus 放行时也可能是 5xx）；流式响应在响应头到达时记账。
        // 回报放在 try 之外，策略异常不会被记成一次尝试失败。
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

    /// <summary>熔断的计数维度：<c>host:port</c>（同一 host 的不同端口是两个独立下游），非分层 URI 退化为 host。</summary>
    private static string CircuitHost(Uri uri)
        => uri.Authority is { Length: > 0 } authority ? authority : uri.Host;

    /// <summary>库层面的「不该重试」：次数到顶、调用方已取消、错误即取消、被熔断拦下；其余交给策略判断。</summary>
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
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCanceled })
        {
            return false;
        }

        if (context.Exception is AxiosException { Code: AxiosException.ErrCircuitOpen })
        {
            // 熔断拒绝时请求根本没发出去，再重试只会撞同一个开着的闸门。
            return false;
        }

        return policy.ShouldRetry(context);
    }

    /// <summary>按模板复制一条可发送的请求（请求头逐个搬运，正文沿用同一份）；用 <c>TryAddWithoutValidation</c> 避免对已校验过的头再校验一遍。</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage template)
    {
        var request = new HttpRequestMessage(template.Method, template.RequestUri)
        {
            Version = template.Version,

            // 策略必须一起搬，否则 RequestVersionExact 会变回默认的 OrLower。
            VersionPolicy = template.VersionPolicy
        };

        foreach (var header in template.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = template.Content;
        return request;
    }

    /// <summary>一次尝试的时间预算：超时令牌源 + 流式响应的剩余预算计时器。粒度是一次尝试，因此每次重试各有独立预算。无超时时 <see cref="Token"/> 直接是调用方令牌，不建 linked CTS。</summary>
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

        /// <summary>流式响应的剩余预算：只有「流式 + 有超时」才有值；timeout 要延续到正文读完，故记录起点算剩余。</summary>
        public TimeSpan? StreamRemaining => _clock is null ? null : _timeout - _clock.Elapsed;

        /// <summary>取消类异常的归属：调用方取消 → <c>ERR_CANCELED</c>，否则本次尝试超时 → <c>ERR_TIMEOUT</c>。</summary>
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
                // CancelAfter 上限约 49.7 天，越界会抛裸异常，故收敛到上限。
                timeoutCts.CancelAfter(Timeouts.Clamp(timeout));
            }

            // 流式 + 有超时才用得上计时器（口径见 StreamRemaining）。
            var clock = streamResponse && hasTimeout ? Stopwatch.StartNew() : null;

            return new AttemptBudget(timeoutCts, externalToken, clock, timeout);
        }
    }

    /// <summary>一次尝试的完整生命周期：占并发槽位 → 建超时预算 → 取连接 → 发送 → 读取 → 转换。并发槽位与连接租约都在 finally 归还；流式响应的租约、消息与槽位随正文流转交调用方。</summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendCoreAsync(
        HttpRequestMessage request,
        SendContext ctx)
    {
        var config = ctx.Config;

        PoolLease? lease = null;

        // 流式正文的所有权随后随返回值交给调用方（拦截器抛异常时由调用方兜底释放）。
        IDisposable? streamOwner = null;

        // 并发槽位：与 lease 同一释放点；声明在 try 之外，取值在 try 之内（占位后任何失败都要归还）。
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
            var (response, statusAccepted) = await ReadResponseAsync(responseMessage, request, ctx, budget).ConfigureAwait(false);

            // 阶段 6：转换 —— 状态码 → 异常；流式正文 → 计时流 + 资源所有权转移。
            // stream 模式下消息不能释放（正文流从它取出），所有权转移给 ResponseStreamOwner。
            if (!ctx.StreamResponse)
            {
                responseMessage.Dispose();
            }

            if (!statusAccepted)
            {
                if (ctx.StreamResponse)
                {
                    // 失败响应没人读正文：先抢救一段错误体文本，再释放内容把连接还回池。
                    response.RawText = await ReadStreamErrorBodyAsync(response.Data as Stream, config, budget.Token).ConfigureAwait(false);

                    (response.Data as IDisposable)?.Dispose();
                    response.Data = null;
                    responseMessage.Dispose();
                }

                throw AxiosException.FromResponse(config, request, response);
            }

            if (ctx.StreamResponse && response.Data is Stream body)
            {
                // 流式正文超时口径：设了 StreamIdleTimeout 时正文按空闲超时，否则共享剩余预算。
                Stream wrapped = config.LimitsOrNull?.StreamIdleTimeout is { } idleTimeout
                    ? new TimeoutStream(
                        body,
                        remaining: null,
                        ctx.ExternalToken,
                        config,
                        request,
                        idleTimeout > TimeSpan.Zero ? idleTimeout : null)
                    : new TimeoutStream(body, budget.StreamRemaining, ctx.ExternalToken, config, request);

                // 租约、消息与并发槽位一并转移给流：下载期间该 origin 仍算活跃，流 Dispose 时统一释放。
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
            // 池在请求在途时被释放：翻译成「请求被中止」。
            throw AxiosException.Canceled(config, request, "客户端或其连接池在请求在途时被释放，请求被中止", ex);
        }
        finally
        {
            lease?.Dispose();

            // 槽位在所有失败路径上都归还；流式成功路径已把它转交给正文流（slot = null）。
            slot?.Dispose();
        }
    }

    /// <summary>阶段 1 —— 并发槽位：排队时间不计入 <see cref="AxiosConfig.Timeout"/>（队列有自己的 <see cref="AxiosConfig.LimitSettings.QueueTimeout"/>）；返回值由调用方在 finally 归还。</summary>
    private static async ValueTask<IDisposable?> EnterSlotAsync(HttpRequestMessage request, SendContext ctx)
    {
        if (ctx.Gate is not { } gate)
        {
            return null;
        }

        return await gate.AcquireAsync(ctx.Config, request, ctx.ExternalToken, ctx.Config.LimitsOrNull?.QueueTimeout).ConfigureAwait(false);
    }

    /// <summary>阶段 3 —— 取连接：注入 Handler 时固定用它的 <see cref="HttpClient"/>，否则按 origin 租一条；是否新建 origin 节点写进 span。</summary>
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

    /// <summary>阶段 4 —— 发送：只到响应头（<see cref="HttpCompletionOption.ResponseHeadersRead"/>），正文留给读取阶段；取消 / 超时、网络与库异常都在这里翻译成统一错误模型。</summary>
    private static async Task<HttpResponseMessage> SendRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        SendContext ctx,
        AttemptBudget budget)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw budget.Translate(ex, ctx.Config, request);
        }
        catch (HttpRequestException ex) when (FindAxiosException(ex) is { } translated)
        {
            // 正文序列化 / 上传回调抛出的库异常被 HttpClient 包成 HttpRequestException：原样还原，否则会误报 ERR_NETWORK。
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

    /// <summary>阶段 5 —— 读取：先判定状态码接受性，再把正文读成 <see cref="AxiosResponse"/>；接受性只判一次，不合法时按宽松口径解析，任何失败路径都先释放响应消息。</summary>
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
            // 自定义 Handler 的正文流可能直接抛 HttpRequestException，与 IO 失败同一错误码。
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

    /// <summary>沿 <see cref="Exception.InnerException"/> 链找库自己的异常（HttpClient 会把正文序列化异常包成 <see cref="HttpRequestException"/>）。</summary>
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

    /// <summary>响应拦截器返回 <c>null</c>：返回类型非空，就地报错。</summary>
    private static AxiosException NullInterceptorResult(AxiosConfig config)
        => AxiosException.InvalidOption(
            "响应拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosResponse 实例"
            + "（要中断这次请求请抛异常，不要返回 null）。",
            config);

    /// <summary>流式失败响应保留的错误体上限。</summary>
    private const int StreamErrorBodyLimit = 64 * 1024;

    /// <summary>把流式失败响应的正文读成文本（最多 <see cref="StreamErrorBodyLimit"/> 字节）；读取失败 / 超时 / 超限都只保留已读部分。</summary>
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

        // 缓冲走 ArrayPool（Rent 可能更大），读取上限仍按 limit。
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
    /// 「明确要求 HTTP/3」时的前置校验：只拦 <c>RequestVersionExact</c> / <c>RequestVersionOrHigher</c>，
    /// <c>RequestVersionOrLower</c> 的降级是正常路径；判据是本机能力 <see cref="Http3Support"/>，做不到就报配置错误。
    /// </summary>
    private static void ValidateHttp3Request(Uri uri, Version version, HttpVersionPolicy policy, AxiosConfig config)
    {
        if (version.Major < 3 || policy == HttpVersionPolicy.RequestVersionOrLower)
        {
            return;
        }

        // 明文目标上不存在 h3（QUIC 自带 TLS 1.3）。
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

    /// <summary>限制类配置的取值范围校验：负值一律报错，不静默当作「不限制」；<c>Timeout=0</c> 不超时，<c>MaxRate=0</c> 不限速。</summary>
    private static void ValidateRangeConfig(AxiosConfig config)
    {
        if (config.Timeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"Timeout 不能为负（当前 {config.Timeout}）：要关闭超时请显式设为 TimeSpan.Zero。", config);
        }

        var limits = config.LimitsOrNull;

        if (limits?.StreamIdleTimeout is { } idleTimeout && idleTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"StreamIdleTimeout 不能为负（当前 {idleTimeout}）：要不设空闲超时请显式设为 TimeSpan.Zero。", config);
        }

        if (limits?.MaxRate is < 0)
        {
            throw AxiosException.InvalidOption($"MaxRate 不能为负（当前 {limits.MaxRate}）：要不限速请设为 null（0 等价于不限速）。", config);
        }

        if (limits?.MaxContentLength is < 0)
        {
            throw AxiosException.InvalidOption($"MaxContentLength 不能为负（当前 {limits.MaxContentLength}）：不限制请设为 null。", config);
        }

        if (limits?.MaxBodyLength is < 0)
        {
            throw AxiosException.InvalidOption($"MaxBodyLength 不能为负（当前 {limits.MaxBodyLength}）：不限制请设为 null。", config);
        }

        // 并发上限：0 与负数都没有语义，静默当成「不限制」会让调用方以为限流生效了。
        if (limits?.MaxConcurrentRequests is int maxConcurrency && maxConcurrency < 1)
        {
            throw AxiosException.InvalidOption(
                $"MaxConcurrentRequests 必须是正整数（当前 {maxConcurrency}）：不限制请设为 null，"
                + "要「不排队」请把 QueueTimeout 设为 TimeSpan.Zero。", config);
        }

        if (limits?.QueueTimeout is { } queueTimeout && queueTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"QueueTimeout 不能为负（当前 {queueTimeout}）：要不排队请设为 TimeSpan.Zero，要一直等请设为 null。", config);
        }

        // 分帧上限：0 没有「不限制」语义，也不能溢出 int，越界在发送前就报。
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

        // 重试策略的取值范围：自定义策略只能校验 MaxRetries，内置实现连等待时间一起校验。
        if (config.RetryOrNull?.Policy is { } retry)
        {
            if (retry.MaxRetries < 0)
            {
                throw AxiosException.InvalidOption($"重试次数 MaxRetries 不能为负（当前 {retry.MaxRetries}）：不重试请设为 0 或不要设置 Retry.Policy。", config);
            }

            if (retry is RetryPolicy policy)
            {
                if (policy.Delay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption($"重试间隔 Delay 不能为负（当前 {policy.Delay}）：要立即重试请设为 TimeSpan.Zero。", config);
                }

                if (policy.MaxDelay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption($"重试间隔上限 MaxDelay 不能为负（当前 {policy.MaxDelay}）：不封顶请设为一个较大的值（上限约 49.7 天）。", config);
                }

                // MaxDelay 是显式配置的上限，超过计时器范围直接报错而不截断。
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

        // 熔断策略的取值范围：这些值都没有可执行语义，静默取默认值会让人以为配置生效了。
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

    /// <summary>解析（必要时懒创建）本实例的并发闸门。信号量容量创建时固定、无法重建，所以「本次生效上限 ≠ 闸门容量」一律报错。未设置时不建闸门；实例已有闸门时按闸门走（<c>null</c> 视为「本次没表态」）。</summary>
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

    /// <summary>实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：按请求复述实例的等价设置放行，写入别的值报错（WithCredentials / MaxRedirects 按值比，其余按引用比）。</summary>
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
            throw AxiosException.InvalidOption("Credentials 是实例级配置，请在 AxiosClient 的 defaults 或 ConnectionPoolOptions 上设置。", config);
        }

        // 池在构造时已经建好，按请求改它只会被静默丢弃，故与 Proxy / MaxRedirects 一致地报错。
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
    /// <c>WithCredentials</c> 只允许复述实例生效值；注入自定义 Handler 时只要写就报错。
    /// 生效值取池的 <see cref="ConnectionPoolOptions.UseCookies"/>，并先按 <see cref="AxiosConfig.WithCredentialsSet"/> 早退。
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

        // 本次没写这一项直接放行：池已按构造期设置固化。
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

    /// <summary><c>MaxRedirects</c> 只允许写与实例生效值相同的值；生效值取池的实际策略（<c>AllowAutoRedirect = false</c> 时视为 0）。</summary>
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

        // 未显式写过直接放行；不能拿属性默认值 5 做相等短路，它与池生效值可能背离。
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

    /// <summary>释放实例：不打断在途请求，资源释放推迟到最后一个请求结束；此后新请求抛 <c>ERR_CANCELED</c>。</summary>
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

    /// <summary>与 <see cref="Dispose"/> 等价；连接池关闭本身是同步的，实现它只为支持 <c>await using</c>。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
