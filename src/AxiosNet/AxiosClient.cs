using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    /// 对应 axios instance.defaults。
    /// <para>
    /// 连接级选项（<see cref="AxiosConfig.ConnectionSettings.WithCredentials"/> /
    /// <see cref="AxiosConfig.ConnectionSettings.MaxRedirects"/> /
    /// <see cref="AxiosConfig.ConnectionSettings.Proxy"/> / <see cref="AxiosConfig.ConnectionSettings.Credentials"/> /
    /// <see cref="AxiosConfig.ConnectionSettings.PoolOptions"/>）在<b>构造时已固化</b>进连接池 / Handler，
    /// 构造后再修改不生效（校验与池配置都以构造期快照为准，不会有任何报错提示）——
    /// 要改变它们请新建实例。其余配置（头、transform、超时等）随时可改。
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

        // 一律按「本实例的生效配置」算：回放 response.Config 时，其中的 BaseAddress 等实例级项
        // 由 MergeConfig 的 instanceSourced 门禁让位给本实例默认值，与 RequestAsync 实际发出的地址一致。
        // （此前「生效配置原样使用」那个分支是为了让 GetUri(Action) 里写的 BaseAddress 不被门禁吞掉，
        //  那个需求现在由 GetUri(Action) 直接拼装满足，不再需要它。）
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
    /// 把调用方交进来的配置整理成「本次请求的生效配置」：普通配置（按请求补丁）原样返回，
    /// 由 <see cref="SendAsync"/> 合并到实例默认值。
    /// <para>
    /// 带 <see cref="AxiosConfig.EffectiveMerged"/> 的生效配置（典型是回放 <see cref="AxiosResponse.Config"/>）
    /// 在这里<b>直接合并</b>：合并只读 perCall、不写它，不需要先 <see cref="AxiosConfig.Clone"/> 一份 ——
    /// 那份克隆只是用来「清掉标记让 SendAsync 走合并分支」，白白多一次深拷。
    /// </para>
    /// <para>
    /// 合并产物在这里（而不是 <c>MergeConfig</c> 里）置 <c>EffectiveMerged</c>：
    /// 标记的含义是「本次请求已合并、SendAsync 不必再合并」，只对这条路径成立；
    /// <c>MergeConfig</c> 的其他调用方（SendAsync 的常规合并、<c>GetUri</c>）拿到的产物
    /// 仍按「未合并的输入」处理，与 <c>GetUri</c> 既有的 <c>EffectiveMerged ? 原样 : 合并</c> 判定兼容。
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
    /// 请求入口：已释放拦截、在途计数（配合 <see cref="Dispose"/> 的推迟释放）、合并与校验，
    /// 然后跑完整管线（拦截器 → 编码 → 发送 → 读取 → 转换）。
    /// <paramref name="target"/> 是调用方预造的响应对象，管线就地填充。
    /// <para>守卫与计数归还必须成对：任何一处抛异常都靠 finally 归还最后一条租约。</para>
    /// </summary>
    private async Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // ObjectDisposedException.ThrowIf 是 .NET 7 才有的 API，手写以兼容 net6.0 目标。
        if (Volatile.Read(ref _disposed) == 1)
        {
            // 统一错误模型：已释放的实例不再漏出裸 ObjectDisposedException
            throw AxiosException.Canceled(
                perCallConfig,
                null,
                DisposedMessage,
                new ObjectDisposedException(nameof(AxiosClient)));
        }

        // 先占位再复核：请求要么完整跑在释放之前，要么明确收到错误，
        // 不会出现「拿着已释放的 Handler/池跑到一半」的中间态。
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _disposed) == 1)
        {
            ReleaseInFlight();
            throw AxiosException.Canceled(
                perCallConfig,
                null,
                DisposedMessage,
                new ObjectDisposedException(nameof(AxiosClient)));
        }

        // 遥测状态（全部是局部量：关闭遥测时不产生任何分配）
        //   telemetryEnabled —— 本次请求是否产出遥测（在途计数、耗时、span、事件都看它）；
        //   telemetryStart   —— 单调时钟起点，指标 / 事件 / span 共用（三者必须量同一段）；
        //   telemetryUri     —— 请求成型的标志：为 null 说明配置校验或拦截器就失败了，那不是一次 HTTP 操作；
        //   telemetryActivity —— 没有监听者时为 null，后续调用一路判空即可，不需要额外分支。
        // 后三个都在 URL 拼好之后一起定下来（见步骤 3），判定开关也放在同一处 —— 理由见那里的注释。
        AxiosConfig? config = null;
        Uri? telemetryUri = null;
        var telemetryEnabled = false;
        var telemetryStart = 0L;
        Activity? telemetryActivity = null;

        try
        {
            // 生效配置：快捷方法传进来的已合并好（见 Shortcut），其余入口在这里合并到实例默认值。
            // 校验一律对生效配置做：两种来源的判定条件相同。
            config = perCallConfig.EffectiveMerged ? perCallConfig : MergeConfig(perCallConfig);
            ValidatePerRequestOptions(config);

            // 1) 请求拦截器：与 axios 一致，按注册逆序执行
            //    注意：实例级选项（Proxy / Credentials / WithCredentials / MaxRedirects / PoolOptions）
            //    在构造期已固化进连接池，拦截器里改它们不生效也不报错 —— 请新建实例配置
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

            // 泛型 <dynamic>/<object> 与 <Stream> 的响应形态自动切换（对标 responseType:
            // 'dynamic' / 'stream'）。判据是 ResponseTypeSet（调用方显式设置过）而不是当前值：
            // Json 是字段默认值，只看值会把「Defaults 上显式设过 Json」也当成未表态。
            // 位置必须在请求拦截器之后：拦截器可以整体替换 config（返回一个新 AxiosConfig），
            // 在它之前切换会在拦截器换配置时静默丢失，最终在反序列化阶段报 ERR_BAD_RESPONSE ——
            // 错误表象与真实原因（拦截器换了配置）相距甚远（与下面的 ValidateRangeConfig 同理）。
            // 自动切换不置 ResponseTypeSet：否则回放生效配置时会被当成调用方显式意图。
            if (!config.ResponseTypeSet)
            {
                if (dataType == typeof(object))
                {
                    config.SetResponseTypeAuto(ResponseType.Dynamic);
                }
                else if (dataType == typeof(Stream))
                {
                    config.SetResponseTypeAuto(ResponseType.Stream);
                }
            }

            // 负值没有合理语义，静默当作「不限制」会让调用方以为限制生效了。
            // 必须校验**拦截器之后**的生效配置：拦截器可以替换 / 改写 config，
            // 在校验前跑就等于允许「拦截器写 Timeout = -1」绕过护栏（hasTimeout 随后为 false，
            // 静默变成不超时）。放在这里也允许拦截器修正调用方的非法值。
            ValidateRangeConfig(config);

            // 2) transformRequest：拦截器之后、编码之前依次作用于数据
            // 先判空：TransformRequest 延迟创建，空链不值得为一次枚举建出集合与快照。
            // 快照访问器拿具体 List：foreach 走结构体枚举器，不装箱。
            if (config.TransformRequestSnapshot is { } requestTransforms)
            {
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

            // 3) 构造 URI 与「请求模板」
            //    正文与请求头只准备一次：重试必须换一条新的 HttpRequestMessage（同一实例发不了两次），
            //    但正文继续复用 —— 上传进度计量器与限速状态都挂在正文上，换一份等于进度清零、白序列化一遍。
            //    没有重试策略时模板本身就是唯一那条请求，与不重试的旧路径完全一致（不多一次分配）。
            var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.ParamsSerializer, config: config);

            // 遥测的判定与起点都在这里，三件事同一个理由：
            //   ① 口径一致 —— 指标与 span 必须量同一段，而 span 需要 URL（标签里要 host / path），只能到这儿才开；
            //   ② 单一判定 —— 请求拦截器可以整体替换 config，开关必须看**最终**生效配置，提前缓存会看错；
            //   ③ 在途数只数「真的进了发送阶段」的请求 —— 拦截器 / transformRequest 的准备阶段不算占用。
            // 代价是「准备阶段就失败」的请求没有遥测（配置校验 / 拦截器 / 序列化失败）：那不是一次 HTTP 操作，
            // 计进接口耗时反而会污染统计。没有监听者时 StartRequest 返回 null，后续所有遥测调用都是判空直通。
            telemetryUri = uri;
            if (AxiosTelemetry.IsEnabled(config))
            {
                telemetryEnabled = true;
                telemetryStart = AxiosTelemetry.Timestamp;
                AxiosTelemetry.EnterRequest();
                telemetryActivity = AxiosTelemetry.StartRequest(config, uri);
            }

            // 默认 HTTP/2：现在 h2 是公网流量的主力（占比约一半，1.x 已降到一成多），默认停在 1.1
            // 等于让绝大多数调用方白白丢掉多路复用与头压缩。默认 2.0 不会伤到老服务 —— 降级发生在
            // TLS 握手的 ALPN 里，服务端只认 http/1.1 就直接谈成 1.1，不多一次往返、不多一条连接、
            // 也没有「先试 2.0 再回退」的探测请求；协商不上时用户根本感知不到（代价只是没拿到 h2 的收益）。
            // 策略显式写出来（而不是依赖 BCL 默认值）：「不支持就降级」是库的契约，BCL 哪天改了默认策略
            // （例如改 Exact）不能让这条承诺静默失效 —— 一旦变成 Exact，默认 2.0 就会让所有老服务直接失败。
            // 明文 HTTP 上没有 ALPN 可协商，OrLower 一律走 1.1，不会拿 h2c 先验知识去撞只认 1.x 的服务端；
            // 也就是说默认 2.0 只影响 https，http:// 的线上行为与之前完全一致。
            // 反向（需要强制 1.1：老网关、NTLM/Negotiate 这类连接级认证、必须用 Connection/Keep-Alive
            // 等 h2 禁止的连接级头）：Connection.HttpVersion = Version11。
            var connection = config.ConnectionOrNull;
            var template = new HttpRequestMessage(config.Method, uri)
            {
                Version = connection?.HttpVersion ?? HttpVersion.Version20,
                VersionPolicy = connection?.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower
            };

            // template 一旦创建就持有请求体（可能是调用方的 FileStream / FormFile），
            // 所以从这里到方法结束的所有路径都必须先释放它。
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

                var externalToken = config.CancellationToken;
                var streamResponse = config.ResponseType == ResponseType.Stream;

                // 重试配置：没有策略 = 不重试（默认），下面的发送循环退化为一次尝试。
                var retry = config.RetryOrNull;

                // 正文可重放性：重试复用同一份正文，来自流的正文必须能拨回起点，
                // 否则第二次发送只会发出空内容 —— 在发请求之前就拦下，而不是等重试时才发现。
                var bodyRewind = retry?.Policy is { MaxRetries: > 0 } ? RetryBody.Prepare(config) : null;

                // 流式正文的所有权交给调用方：拦截器抛异常或丢弃响应时由这里兜底释放，
                // 否则租约永不归还，该 origin 节点再也不会被驱逐。
                IDisposable? streamOwner = null;

                // 5) 响应拦截器：按注册顺序包装成责任链
                //    重试在链内（见 SendWithRetryAsync）：拦截器只看到最终结果，不会因重试被重复执行。
                var responseInterceptors = Interceptors.Response.Snapshot();
                AxiosResponse? result;
                try
                {
                    if (responseInterceptors.Length == 0)
                    {
                        // 空链：直接调用，省掉闭包 + 委托 + 责任链包装的每请求分配。
                        var (direct, owner) = await SendWithRetryAsync(
                                template, config, uri, externalToken, dataType, target, streamResponse, retry, bodyRewind)
                            .ConfigureAwait(false);

                        streamOwner = owner;
                        result = direct;
                    }
                    else
                    {
                        Func<Task<AxiosResponse>> invoker = async () =>
                        {
                            var (invoked, owner) = await SendWithRetryAsync(
                                    template, config, uri, externalToken, dataType, target, streamResponse, retry, bodyRewind)
                                .ConfigureAwait(false);

                            streamOwner = owner;
                            return invoked;
                        };

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

                        result = await chain().ConfigureAwait(false);
                    }
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

                // 拦截器返回了新实例：原流式正文已无引用，需要在这里释放。
                if (streamOwner is not null && !ReferenceEquals(result?.Data, streamOwner))
                {
                    streamOwner.Dispose();
                }

                if (telemetryEnabled)
                {
                    // 必须在 template.Dispose() 之前（外层 finally）：那之后正文头已不可读。
                    AxiosTelemetry.RequestSucceeded(config, uri, result!, telemetryStart, telemetryActivity);
                }

                return result!;
            }
            finally
            {
                // 正文随模板一起释放：所有尝试共享同一份正文（见 RetryBody），
                // 中途换出来的请求只摘不释放（SendWithRetryAsync 的 finally）。
                template.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 遥测不该改变异常本身：记完原样抛（连栈都不动，这里不包装）。
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

            // 收 span：即使上面抛异常也要收掉，否则它会一直挂在 Activity.Current 上，
            // 后续请求的 span 会错误地挂到它下面（helper 内部兜住监听回调的异常）。
            AxiosTelemetry.StopRequest(telemetryActivity);

            ReleaseInFlight();
        }
    }

    /// <summary>
    /// 带重试的发送入口：<b>没有策略时零开销</b>——直接把单次发送的 Task 交回去，
    /// 不为「一次都不重试」的循环建状态机（热路径上每个请求都建一个纯属白分配）。
    /// </summary>
    private Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendWithRetryAsync(
        HttpRequestMessage template,
        AxiosConfig config,
        Uri uri,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse,
        AxiosConfig.RetrySettings? retry,
        RetryBody? bodyRewind)
    {
        var policy = retry?.Policy;
        if (policy is null || policy.MaxRetries <= 0)
        {
            return SendCoreAsync(template, config, uri, externalToken, dataType, target, streamResponse);
        }

        return SendRetryLoopAsync(
            template, config, uri, externalToken, dataType, target, streamResponse, retry!, policy, bodyRewind);
    }

    /// <summary>
    /// 重试循环（只在配置了策略时才会走到）。
    /// <para>
    /// 每次尝试换一条新的 <see cref="HttpRequestMessage"/>（同一实例不能发两次），正文与请求头沿用模板。
    /// 每次尝试有<b>独立的超时预算</b>（<see cref="AxiosConfig.Timeout"/> 是单次尝试的上限，不是全部重试的总预算），
    /// 调用方的 <see cref="AxiosConfig.CancellationToken"/> 则贯穿所有尝试与等待。
    /// </para>
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendRetryLoopAsync(
        HttpRequestMessage template,
        AxiosConfig config,
        Uri uri,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse,
        AxiosConfig.RetrySettings retry,
        IAxiosRetryPolicy policy,
        RetryBody? bodyRewind)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // 同一份正文再发一次：来自流的正文要先把读指针拨回起点。
                // 放在 try 外面是有意的 —— 拨不动（流被关闭 / 不支持定位）说明正文已不可重放，
                // 这不是「一次失败的尝试」，不该再拿去问策略要不要重试。
                bodyRewind?.Rewind(config);
            }

            // 首次尝试直接用模板，后续尝试按模板复制（模板本身不能再发，也不该被后续尝试改写）
            var request = attempt == 1 ? template : CloneRequest(template);
            try
            {
                return await SendCoreAsync(request, config, uri, externalToken, dataType, target, streamResponse)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消类异常不在这里接：它不该走「问策略要不要重试」这条路，
                // 直接冒到调用方（SendCoreAsync 已把超时/取消翻译成 AxiosException，
                // 能漏到这里的裸 OperationCanceledException 只可能来自调用方取消）。
                var failed = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, config, (ex as AxiosException)?.Response);

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
                    // 与 Timeout 同一个坑：Task.Delay 底层也是计时器队列，延迟上限是 uint.MaxValue-1 毫秒，
                    // 超出会抛裸 ArgumentOutOfRangeException。内置策略的 MaxDelay 有配置期校验兜着，
                    // 但自定义策略可以返回任意值 —— 在这里收敛，保证不漏裸异常（语义仍是「极长但有限」）。
                    delay = Timeouts.Max;
                }

                // 回调与遥测共用同一个上下文（readonly struct，栈上传递不分配）
                var retryContext = new AxiosRetryContext(
                    attempt, policy.MaxRetries, ex, config, failed.Response, delay);

                // 回调在 catch 块里调用（不是 try 里）：它抛出的异常不会被当成「本次尝试失败」
                // 再去问一次策略，而是按 RetrySettings.OnRetry 的约定原样抛给调用方。
                retry.OnRetry?.Invoke(retryContext);

                // 重试计数 / span 的 resend_count 与 retry 事件：观测点放在「决定重试」这一刻，
                // 与 OnRetry 回调同一语义（不含「次数到顶后的最后一次失败」）。
                AxiosTelemetry.RequestRetried(retryContext, uri);

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, externalToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce)
                    {
                        // 等待期间被取消：仍按统一错误模型报「已取消」，而不是漏出裸 TaskCanceledException
                        throw AxiosException.Canceled(config, request, "请求在重试等待期间被取消", oce);
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
    /// 库层面的「不该重试」：次数到顶、调用方已取消、错误本身就是取消。
    /// 其余判断交给策略（<see cref="IAxiosRetryPolicy.ShouldRetry"/>）。
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

        return policy.ShouldRetry(context);
    }

    /// <summary>
    /// 按模板复制一条可发送的请求（请求头逐个复制，正文沿用同一份）。
    /// <para>
    /// 用 <c>TryAddWithoutValidation</c> 而不是赋值：模板上的头已经过 <c>ApplyHeaders</c> 的校验与分流，
    /// 这里只做搬运，不再触发一次校验（也避免 BCL 对个别头的规范化改写）。
    /// </para>
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
    /// 单次发送：建本次尝试的超时令牌，然后发送 + 读取 + 状态码校验。
    /// <para>
    /// 超时源建在这里（而不是调用方）是为了让「每次重试都有独立的超时预算」成为自然结果：
    /// 计时器跟着一次尝试走，而不是从第一次尝试开始算一次总预算。
    /// </para>
    /// </summary>
    private async Task<(AxiosResponse Response, IDisposable? StreamOwner)> SendCoreAsync(
        HttpRequestMessage request,
        AxiosConfig config,
        Uri uri,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse)
    {
        var hasTimeout = config.Timeout > TimeSpan.Zero;

        // 只有「有超时」才需要独立令牌源：无超时时 sendToken 直接复用调用方令牌，
        // 建 linked CTS 换不来任何行为差异，却要每请求付一次 CTS + 注册。
        using CancellationTokenSource? timeoutCts = hasTimeout
            ? (externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource())
            : null;

        if (timeoutCts is not null)
        {
            // CancelAfter 上限约 49.7 天（uint.MaxValue-1 毫秒），越界抛裸 ArgumentOutOfRangeException：
            // 收敛到上限，语义等价于「极长但有限」。
            timeoutCts.CancelAfter(Timeouts.Clamp(config.Timeout));
        }

        // 交给底层每次 I/O 的令牌：无超时时就是调用方令牌（None 时不注册、不分配）。
        var sendToken = timeoutCts?.Token ?? externalToken;

        // 流式响应的 timeout 要延续到正文读完：记录起点用于算剩余预算。
        // 只有「流式 + 有超时」用得上，其余组合下计时器纯属白分配。
        var clock = streamResponse && hasTimeout ? System.Diagnostics.Stopwatch.StartNew() : null;

        PoolLease? lease = null;

        // 流式正文的所有权随后随返回值交给调用方（拦截器抛异常时由调用方兜底释放）。
        IDisposable? streamOwner = null;
        try
        {
            // 注入 Handler：固定 HttpClient；默认实例：按 origin 租用（连接复用 + 并发上限）。
            // 租用也走异步入口：已取消/已超时的请求在租用前就被截断，异常同样翻译成统一错误模型。
            var httpClient = _customClient;
            if (httpClient is null)
            {
                try
                {
                    lease = await _pool!.LeaseAsync(uri, config, sendToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    if (!externalToken.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
                    {
                        throw AxiosException.Timeout(config, request, ex);
                    }

                    throw AxiosException.Canceled(config, request, ex);
                }

                httpClient = lease.Client;
            }

            HttpResponseMessage responseMessage;
            try
            {
                responseMessage = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (!externalToken.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
                {
                    throw AxiosException.Timeout(config, request, ex);
                }

                throw AxiosException.Canceled(config, request, ex);
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
                throw AxiosException.Network(config, request, ex);
            }
            catch (IOException ex)
            {
                throw AxiosException.Network(config, request, ex);
            }
            catch (NotSupportedException ex)
            {
                // 自定义 Handler / Content 不支持某种能力时不要把裸异常漏给调用方。
                throw AxiosException.Network(config, request, ex);
            }

            AxiosResponse response;
            bool statusAccepted;
            try
            {
                // 状态码接受性只判定一次：它同时决定宽松解析与是否抛错，
                // 判两次会让有副作用的 validateStatus（计数、日志）执行两遍。
                var validateStatus = config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
                statusAccepted = validateStatus((int)responseMessage.StatusCode);

                // 状态码不合法时宽松解析：否则 404 + HTML 会在反序列化处抛错，
                // 把本该带 Status/Response 的 ERR_BAD_REQUEST 变成 ERR_BAD_RESPONSE 且两者全丢。
                response = await ResponseReader.ReadAsync(
                        responseMessage,
                        request,
                        config,
                        sendToken,
                        dataType,
                        target,
                        lenient: !statusAccepted)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                responseMessage.Dispose();
                if (!externalToken.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
                {
                    throw AxiosException.Timeout(config, request, ex);
                }

                throw AxiosException.Canceled(config, request, ex);
            }
            catch (IOException ex)
            {
                // 连接中途断开 / 正文被截断（HttpIOException）：统一成 ERR_NETWORK。
                responseMessage.Dispose();
                throw AxiosException.Network(config, request, ex);
            }
            catch (HttpRequestException ex)
            {
                // 自定义 Handler 的正文流可能直接抛 HttpRequestException（BCL 抛 HttpIOException）：
                // 与上面的 IO 失败同一错误码，不能让裸异常穿过统一错误模型。
                responseMessage.Dispose();
                throw AxiosException.Network(config, request, ex);
            }
            catch
            {
                // 读取/解析失败（含长度超限、反序列化失败）：先释放响应，避免连接泄漏
                responseMessage.Dispose();
                throw;
            }

            // stream 模式下消息不能释放（正文流从它取出），所有权转移给 ResponseStreamOwner。
            if (!streamResponse)
            {
                responseMessage.Dispose();
            }

            if (!statusAccepted)
            {
                if (streamResponse)
                {
                    // 失败响应没人读正文：先把一段错误体抢救成文本（ApiErrorContext 的唯一来源），
                    // 再立即释放内容把连接还回池，否则这条连接会被一个永远不会被读取的流一直占住。
                    response.RawText = await ReadStreamErrorBodyAsync(
                            response.Data as Stream, config, sendToken)
                        .ConfigureAwait(false);

                    (response.Data as IDisposable)?.Dispose();
                    response.Data = null;
                    responseMessage.Dispose();
                }

                throw AxiosException.FromResponse(config, request, response);
            }

            if (streamResponse && response.Data is Stream body)
            {
                // 流式正文的超时口径：显式 StreamIdleTimeout 时「Timeout 只到响应头」，
                // 正文按空闲超时（或完全不限）；未设置时保持「整条流共享剩余预算」。
                Stream wrapped = config.LimitsOrNull?.StreamIdleTimeout is { } idleTimeout
                    ? new TimeoutStream(
                        body,
                        remaining: null,
                        externalToken,
                        config,
                        request,
                        idleTimeout > TimeSpan.Zero ? idleTimeout : null)
                    : new TimeoutStream(
                        body,
                        clock is not null ? config.Timeout - clock.Elapsed : null,
                        externalToken,
                        config,
                        request);

                // 租约与 HttpResponseMessage 一并转移给流：下载期间该 origin 仍算活跃，
                // 流 Dispose 时统一释放（否则消息本体永久悬空）。
                wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease);
                lease = null;

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
    /// 把流式失败响应的正文读成文本（最多 <see cref="StreamErrorBodyLimit"/> 字节）。
    /// <para>
    /// 状态码错误时正文里通常就是错误详情；不读的话调用方只能看到一句 "status code 400"。
    /// 读取失败 / 超时 / 超限都只保留已读部分，不掩盖原始 HTTP 错误。
    /// </para>
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

        // 失败路径的一次性缓冲：走 ArrayPool，大错误体 + 高频重试时不再每请求一块 64KB。
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

        // 分帧上限：既不能「设 0 当不限制」（分帧器没有关闭语义），也不能大到溢出 int。
        // 越界在发送前就报，而不是让服务端第一次吐出大帧时在分帧器里炸成裸异常。
        // 三个边界值都在 LimitSettings 上（默认 / 下界 / 上界同处一地），这里只负责比对。
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

        // 重试策略的取值范围：次数为负没有语义（「负一次重试」无从执行），等待时间为负同样无意义。
        // 自定义策略只能校验接口上暴露的 MaxRetries；内置实现连等待时间一起校验。
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

                // 等待走 Task.Delay，与 CancellationTokenSource.CancelAfter 同一个计时器队列（上限约 49.7 天）。
                // 超限时若等到发送阶段才发现，Task.Delay 会抛裸 ArgumentOutOfRangeException；
                // 这里在请求发出之前就明确报配置错误（与 Timeout 的收敛不同：MaxDelay 是显式配置的上限，
                // 静默截断会让调用方以为「最多等这么久」的承诺仍然成立）。
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
    }

    /// <summary>
    /// 实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：
    /// 按请求复述实例的等价设置放行，写入别的值报错。
    /// <para>
    /// 等价判定分两类：<see cref="AxiosConfig.ConnectionSettings.WithCredentials"/> /
    /// <see cref="AxiosConfig.ConnectionSettings.MaxRedirects"/> 是值类型，按<b>值</b>比较；
    /// <see cref="AxiosConfig.ConnectionSettings.Proxy"/> / <see cref="AxiosConfig.ConnectionSettings.Credentials"/> /
    /// <see cref="AxiosConfig.ConnectionSettings.PoolOptions"/> 没有可靠的等值定义（代理/凭据/连接选项的「相同」取决于
    /// 各自实现），按<b>引用</b>比较 —— 即只有「把实例 defaults 上的同一个对象再写一遍」才放行。
    /// </para>
    /// <para>判定对象是合并后的生效配置，因此只需比对，不用追是谁写的。</para>
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
    /// <c>WithCredentials</c> 只允许复述实例生效值（值相同即等价，不必报错）；
    /// 注入自定义 Handler 时无从判断是否等价，只要写就报错。
    /// <para>
    /// 「实例生效值」取连接池的 <see cref="ConnectionPoolOptions.UseCookies"/>，与
    /// <see cref="ValidateMaxRedirects"/> 取池的 <c>AllowAutoRedirect</c> 同一口径 —— 不能读
    /// <c>Defaults.WithCredentials</c>：连接级选项在构造时固化进池，构造后再改 <c>Defaults</c>
    /// 并不生效（文档承诺「静默不生效、不报错」），拿它当生效值会让校验结论与线上行为相反
    /// （按请求复述池的真实值反被拒，报错文案还声称一个从未生效的值是「实例生效值」）。
    /// 共享池同理：Cookie 容器由池决定，与本实例 defaults 无关。
    /// </para>
    /// <para>
    /// 必须先按 <see cref="AxiosConfig.WithCredentialsSet"/> 早退：合并产物会清掉继承来的标记
    /// （见 <c>MergeConfig</c> 的 <c>ClearInstanceOptionFlags</c>），此处的标记只代表「按请求显式写过」。
    /// 不早退就会让「构造后改过 Defaults」的实例在<b>每个</b>普通请求上都被判成写入了不同的值。
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

        // 未显式写过（含快捷方法从 Defaults.Clone() 继承的未设置态）直接放行；
        // 显式写过必须与池的生效值比对。不能拿属性默认值 5 做相等短路：
        // poolOptions 配了别的重定向策略时，Defaults.MaxRedirects（未设置 = 5）与池生效值背离，
        // 按请求显式写的值会被静默放行（文档承诺抛 ERR_BAD_OPTION_VALUE）。
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
