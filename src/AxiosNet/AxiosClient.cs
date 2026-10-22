using System.Runtime.ExceptionServices;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pooling;

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
    /// 连接级选项（<see cref="AxiosConfig.WithCredentials"/> / <see cref="AxiosConfig.MaxRedirects"/> /
    /// <see cref="AxiosConfig.Proxy"/> / <see cref="AxiosConfig.Credentials"/> /
    /// <see cref="AxiosConfig.PoolOptions"/>）在<b>构造时已固化</b>进连接池 / Handler，
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

            if (poolOptions is not null || Defaults.PoolOptions is not null)
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
            if (poolOptions is not null || Defaults.PoolOptions is not null)
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
            if (poolOptions is not null && Defaults.PoolOptions is not null)
            {
                throw AxiosException.InvalidOption(
                    "不能同时传入 poolOptions 和 defaults.PoolOptions：两者描述的是同一个自建连接池，请只在一处配置。",
                    Defaults);
            }

            var effective = (poolOptions ?? Defaults.PoolOptions ?? new ConnectionPoolOptions()).Clone();

            // 池选项非法时在构造期就带配置抛错（池自身的校验没有 config 可挂），
            // 而不是等池内部抛出 config 为 null 的同类异常。
            HttpConnectionPool.ValidateOptions(effective, Defaults);

            // maxRedirects：0 = 不跟随重定向（对应 axios maxRedirects: 0），> 0 = 覆盖池默认上限。
            // 必须按 *Set 判断：MaxRedirects 未显式设置时读到的也是默认值 5，
            // 无条件生效会把调用方在 poolOptions 上配好的 MaxAutomaticRedirections 悄悄改回 5。
            if (Defaults.MaxRedirectsSet)
            {
                if (Defaults.MaxRedirects < 0)
                {
                    throw AxiosException.InvalidOption("MaxRedirects 不能为负数；不跟随重定向请设为 0。", Defaults);
                }

                if (Defaults.MaxRedirects == 0)
                {
                    effective.AllowAutoRedirect = false;
                }
                else
                {
                    effective.MaxAutomaticRedirections = Defaults.MaxRedirects;
                }
            }

            // defaults 上的代理 / 凭据是自建池的合法输入（与共享池分支的报错策略不同），
            // 但两处都写时按请求校验比对的是 defaults，静默取一个会让写入方以为生效了
            if (poolOptions is not null)
            {
                if (poolOptions.Proxy is not null && Defaults.Proxy is not null)
                {
                    throw AxiosException.InvalidOption(
                        "不能同时在 poolOptions.Proxy 和 defaults.Proxy 上配置代理：请只在一处设置。", Defaults);
                }

                if (poolOptions.Credentials is not null && Defaults.Credentials is not null)
                {
                    throw AxiosException.InvalidOption(
                        "不能同时在 poolOptions.Credentials 和 defaults.Credentials 上配置凭据：请只在一处设置。",
                        Defaults);
                }
            }

            effective.Proxy ??= Defaults.Proxy;
            effective.Credentials ??= Defaults.Credentials;
            if (Defaults.WithCredentials)
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

        if (defaults.Proxy is not null || defaults.Credentials is not null)
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
    /// 按配置拼出最终 URL（含 baseURL 合并与 params 序列化），不发请求。
    /// 返回实际发送形态（已转义），可直接用于签名与日志。
    /// <para><see cref="System.Uri"/> 按 RFC 3986 归一化非保留字符（<c>%41</c>→<c>A</c>），与请求行一致。</para>
    /// </summary>
    public string GetUri(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var merged = MergeConfig(config);
        return UrlBuilder.Build(merged.BaseAddress, merged.Url, merged.Params, merged.ParamsSerializer, config: merged)
            .AbsoluteUri;
    }

    /// <summary>按 Action 配置拼出最终请求 URL，不发请求。</summary>
    public string GetUri(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var config = new AxiosConfig();
        configure(config);
        return GetUri(config);
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
        var config = new AxiosConfig();
        configure(config);
        return SendAsync(config, null);
    }

    /// <summary>用 Action 配置请求的泛型入口。</summary>
    public Task<AxiosResponse<T>> RequestAsync<T>(Action<AxiosConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var config = new AxiosConfig();
        configure(config);
        return SendTypedAsync<T>(config);
    }

    /// <summary>
    /// 把调用方交进来的配置整理成「按请求项」：只有带 <see cref="AxiosConfig.EffectiveMerged"/> 的
    /// 生效配置（典型是回放 <see cref="AxiosResponse.Config"/>）需要处理 ——
    /// 克隆一次清掉标记，让它重新合并到本实例默认值，否则本实例的默认值会被整段跳过；
    /// 同时把其中的实例级选项标成「来源是实例」，合并时沿用本实例的值。
    /// </summary>
    private static AxiosConfig PreparePerCall(AxiosConfig config)
        => config.EffectiveMerged ? config.Clone() : config;

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

        try
        {
            // 生效配置：快捷方法传进来的已合并好（见 Shortcut），其余入口在这里合并到实例默认值。
            // 校验一律对生效配置做：两种来源的判定条件相同。
            var config = perCallConfig.EffectiveMerged ? perCallConfig : MergeConfig(perCallConfig);
            ValidatePerRequestOptions(config);

            // 泛型 <dynamic>/<object>：响应自动展开为动态对象图（对标 response.data.xxx）；
            // 显式设置过其他 responseType 时以显式配置为准。
            // 自动切换不置 ResponseTypeSet：否则回放生效配置时会被当成调用方显式意图。
            if (dataType == typeof(object) && config.ResponseType == ResponseType.Json)
            {
                config.SetResponseTypeAuto(ResponseType.Dynamic);
            }

            // 泛型 <Stream>：自动切流式响应（对标 responseType: 'stream'）；显式配置仍然优先。
            if (dataType == typeof(Stream) && config.ResponseType == ResponseType.Json)
            {
                config.SetResponseTypeAuto(ResponseType.Stream);
            }

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

            // 负值没有合理语义，静默当作「不限制」会让调用方以为限制生效了。
            // 必须校验**拦截器之后**的生效配置：拦截器可以替换 / 改写 config，
            // 在校验前跑就等于允许「拦截器写 Timeout = -1」绕过护栏（hasTimeout 随后为 false，
            // 静默变成不超时）。放在这里也允许拦截器修正调用方的非法值。
            ValidateRangeConfig(config);

            // 2) transformRequest：拦截器之后、编码之前依次作用于数据
            // 先判空：TransformRequest 延迟创建，空链不值得为一次枚举建出集合与快照。
            if (config.HasTransformRequest)
            {
                foreach (var transform in config.TransformRequest)
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

            // 3) 构造 URI 与 HttpRequestMessage
            var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.ParamsSerializer, config: config);
            var request = new HttpRequestMessage(config.Method, uri)
            {
                Version = config.HttpVersion ?? HttpVersion.Version11
            };

            // request 一旦创建就持有请求体（可能是调用方的 FileStream / FormFile），
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
                    throw AxiosException.BadRequestPayload(
                        config,
                        request,
                        config.Data?.GetType().Name ?? "null",
                        ex);
                }

                // 上传进度 / 限速：包一层流量统计（头部复制，序列化逻辑不变）
                if (content is not null && (config.OnUploadProgress is not null || config.MaxRate is > 0))
                {
                    content = new ProgressHttpContent(content, config.OnUploadProgress, config.MaxRate, config);
                }

                if (content is not null)
                {
                    if (config.MaxBodyLength is long maxBody
                        && content.Headers.ContentLength is long bodyLength
                        && bodyLength > maxBody)
                    {
                        content.Dispose();
                        throw AxiosException.MaxBodyLength(config, request, bodyLength, maxBody);
                    }

                    request.Content = content;
                }

                ApplyHeaders(request, config);

                var externalToken = config.CancellationToken;
                var hasTimeout = config.Timeout > TimeSpan.Zero;

                // 无超时且外部令牌不可取消时 linkedCts.Token 恒为 None，建 CTS 换不来任何行为差异。
                using CancellationTokenSource? linkedCts = hasTimeout || externalToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                    : null;

                if (hasTimeout)
                {
                    // CancelAfter 上限约 49.7 天（uint.MaxValue-1 毫秒），越界抛裸 ArgumentOutOfRangeException：
                    // 收敛到上限，语义等价于「极长但有限」。
                    linkedCts!.CancelAfter(Timeouts.Clamp(config.Timeout));
                }

                // 交给底层每次 I/O 的令牌：无超时且无外部取消时就是 None（不注册、不分配）。
                var sendToken = linkedCts?.Token ?? CancellationToken.None;

                var streamResponse = config.ResponseType == ResponseType.Stream;

                // 流式响应的 timeout 要延续到正文读完：记录起点用于算剩余预算。
                // 只有「流式 + 有超时」用得上，其余组合下计时器纯属白分配。
                var clock = streamResponse && hasTimeout ? System.Diagnostics.Stopwatch.StartNew() : null;

                // 流式正文的所有权交给调用方：拦截器抛异常或丢弃响应时由这里兜底释放，
                // 否则租约永不归还，该 origin 节点再也不会被驱逐。
                IDisposable? streamOwner = null;

                // 4) 真正的发送 + 读取 + 状态码校验（异常会进入响应拦截器 rejected 链）
                Func<Task<AxiosResponse>> invoker = async () =>
                {
                    PoolLease? lease = null;
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
                                if (!externalToken.IsCancellationRequested && linkedCts is { IsCancellationRequested: true })
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
                            if (!externalToken.IsCancellationRequested && linkedCts is { IsCancellationRequested: true })
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
                            var validateStatus = config.ValidateStatus ?? DefaultValidateStatus;
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
                            if (!externalToken.IsCancellationRequested && linkedCts is { IsCancellationRequested: true })
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
                                // 失败响应没人读正文：立即释放内容把连接还回池，
                                // 否则这条连接会被一个永远不会被读取的流一直占住（重试几次就能打满池）。
                                (response.Data as IDisposable)?.Dispose();
                                response.Data = null;
                                responseMessage.Dispose();
                            }

                            throw AxiosException.FromResponse(config, request, response);
                        }

                        if (streamResponse && response.Data is Stream body)
                        {
                            // 无条件包装（Timeout=0 也包）：让外部取消与正文 I/O 异常在不同 Timeout 下给出同一类异常。
                            var remaining = clock is not null
                                ? config.Timeout - clock.Elapsed
                                : (TimeSpan?)null;

                            Stream wrapped = new TimeoutStream(body, remaining, externalToken, config, request);

                            // 租约与 HttpResponseMessage 一并转移给流：下载期间该 origin 仍算活跃，
                            // 流 Dispose 时统一释放（否则消息本体永久悬空）。
                            wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease);
                            lease = null;

                            response.Data = wrapped;
                            streamOwner = wrapped;
                        }

                        return response;
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
                };

                // 5) 响应拦截器：按注册顺序包装成责任链
                var chain = invoker;
                foreach (var interceptor in Interceptors.Response.Snapshot())
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

                AxiosResponse? result;
                try
                {
                    result = await chain().ConfigureAwait(false);
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

                return result!;
            }
            finally
            {
                request.Dispose();
            }
        }
        finally
        {
            ReleaseInFlight();
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

        if (config.MaxRate is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxRate 不能为负（当前 {config.MaxRate}）：要不限速请设为 null（0 等价于不限速）。", config);
        }

        if (config.MaxContentLength is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxContentLength 不能为负（当前 {config.MaxContentLength}）：不限制请设为 null。", config);
        }

        if (config.MaxBodyLength is < 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxBodyLength 不能为负（当前 {config.MaxBodyLength}）：不限制请设为 null。", config);
        }
    }

    /// <summary>
    /// 实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：
    /// 按请求复述实例的等价设置放行，写入别的值报错。
    /// <para>
    /// 等价判定分两类：<see cref="AxiosConfig.WithCredentials"/> / <see cref="AxiosConfig.MaxRedirects"/>
    /// 是值类型，按<b>值</b>比较；<see cref="AxiosConfig.Proxy"/> / <see cref="AxiosConfig.Credentials"/> /
    /// <see cref="AxiosConfig.PoolOptions"/> 没有可靠的等值定义（代理/凭据/连接选项的「相同」取决于
    /// 各自实现），按<b>引用</b>比较 —— 即只有「把实例 defaults 上的同一个对象再写一遍」才放行。
    /// </para>
    /// <para>判定对象是合并后的生效配置，因此只需比对，不用追是谁写的。</para>
    /// </summary>
    private void ValidatePerRequestOptions(AxiosConfig config)
    {
        if (config.Proxy is not null && !ReferenceEquals(config.Proxy, Defaults.Proxy))
        {
            throw AxiosException.InvalidOption(
                "Proxy 是实例级配置（连接池在构造 AxiosClient 时创建），请改用 new AxiosClient(new AxiosConfig { Proxy = ... }) "
                + "或 ConnectionPoolOptions.Proxy。", config);
        }

        if (config.Credentials is not null && !ReferenceEquals(config.Credentials, Defaults.Credentials))
        {
            throw AxiosException.InvalidOption(
                "Credentials 是实例级配置，请在 AxiosClient 的 defaults 或 ConnectionPoolOptions 上设置。", config);
        }

        // 池在构造时已经建好，按请求改它只会被静默丢弃 —— 与 Proxy/MaxRedirects 保持一致地报错。
        if (config.PoolOptions is not null && !ReferenceEquals(config.PoolOptions, Defaults.PoolOptions))
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

        if (config.WithCredentials == Defaults.WithCredentials)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"WithCredentials 是实例级配置（影响 Cookie 容器与系统凭据），按请求只能写与实例相同的值；"
            + $"当前按请求为 {config.WithCredentials}，实例生效值为 {Defaults.WithCredentials}。"
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

        if (config.MaxRedirects == effective)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"MaxRedirects 是实例级配置（重定向由连接池 Handler 处理），按请求只能写与实例相同的值；"
            + $"当前按请求为 {config.MaxRedirects}，实例生效值为 {effective}（0 表示不跟随重定向）。"
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
