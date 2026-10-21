using System.Buffers;
using System.Text.Json;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// axios 风格的 HTTP 客户端实例，对应 JS 中的 axios instance。
/// 支持 baseURL / method / headers / params / data / timeout / auth / responseType /
/// validateStatus / 取消令牌 / 请求与响应拦截器，默认连接复用并由连接池统一管理。
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

    /// <summary>对应 axios instance.defaults。</summary>
    public AxiosConfig Defaults { get; }

    /// <summary>对应 axios instance.interceptors。</summary>
    public AxiosInterceptors Interceptors { get; } = new();

    /// <summary>
    /// 该实例使用的连接池；注入自定义 <see cref="HttpMessageHandler"/> 的实例不使用连接池，此时为 <c>null</c>。
    /// </summary>
    public HttpConnectionPool? Pool => _pool;

    /// <summary>是否使用连接池（即未直接注入 Handler）。</summary>
    public bool UsesConnectionPool => _pool is not null;

    /// <param name="defaults">实例默认配置（对应 axios instance.defaults）。</param>
    /// <param name="pool">
    /// 共享连接池；与 <paramref name="poolOptions"/> / <paramref name="handler"/> 互斥（同时传入抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// 池的连接行为（重定向 / 代理 / 凭据 / Cookie / 池选项）在创建池时已固定，
    /// 因此 defaults 上的 <c>MaxRedirects</c> / <c>WithCredentials</c> / <c>Proxy</c> / <c>Credentials</c> /
    /// <c>PoolOptions</c> 同样抛 <c>ERR_BAD_OPTION_VALUE</c>（与注入 handler 的策略一致），
    /// 请在创建池的 <see cref="ConnectionPoolOptions"/> 上配置，或不传 pool 让本实例自建池。
    /// </param>
    /// <param name="poolOptions">本实例自建连接池的选项。</param>
    /// <param name="handler">
    /// 直接注入的 <see cref="HttpMessageHandler"/>（如自定义 <see cref="DelegatingHandler"/> 链、
    /// 测试用内存 Handler）。一旦注入则<b>不创建连接池</b>，所有 origin 共用这一个 Handler，
    /// 因此 <c>maxRedirects</c> / <c>proxy</c> / <c>credentials</c> / <c>withCredentials</c>
    /// 等实例级选项需在 Handler 上自行配置（在 defaults 上设置会抛 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// 想在保留连接池的前提下插入 Handler 链，请改用 <see cref="ConnectionPoolOptions.HandlerFactory"/>。
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

            ValidateHandlerLevelOptions(defaults, Defaults);
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
            ValidateSharedPoolLevelOptions(defaults, Defaults);

            _pool = pool;
            _ownsPool = false;
        }
        else
        {
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
    /// 注入 Handler 时，连接层行为完全由 Handler 决定，实例级选项无法生效 —— 与其静默忽略，不如报错。
    /// </summary>
    private static void ValidateHandlerLevelOptions(AxiosConfig? defaults, AxiosConfig config)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.MaxRedirectsSet)
        {
            throw AxiosException.InvalidOption(
                "注入自定义 handler 时 MaxRedirects 由 handler 决定（重定向在连接层处理），请在 handler 上配置。", config);
        }

        if (defaults.WithCredentialsSet)
        {
            throw AxiosException.InvalidOption(
                "注入自定义 handler 时 WithCredentials 由 handler 决定（Cookie 容器在连接层），请在 handler 上配置。", config);
        }

        if (defaults.Proxy is not null || defaults.Credentials is not null)
        {
            throw AxiosException.InvalidOption(
                "注入自定义 handler 时 Proxy / Credentials 由 handler 决定，请在 handler 上配置。", config);
        }
    }

    /// <summary>
    /// 共享连接池时，池已按自己的 <see cref="ConnectionPoolOptions"/> 创建完成，
    /// defaults 上的连接级配置（重定向 / 凭据 / 代理 / Cookie）与 PoolOptions 都无法生效 —— 直接报错。
    /// </summary>
    private static void ValidateSharedPoolLevelOptions(AxiosConfig? defaults, AxiosConfig config)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.MaxRedirectsSet)
        {
            throw AxiosException.InvalidOption(
                "共享连接池时 MaxRedirects 由池决定（重定向由各 origin 的 Handler 处理），"
                + "请在创建 ConnectionPoolOptions 时设置 AllowAutoRedirect / MaxAutomaticRedirections，"
                + "或不传 pool 让本实例自建池。", config);
        }

        if (defaults.WithCredentialsSet)
        {
            throw AxiosException.InvalidOption(
                "共享连接池时 WithCredentials 由池决定（Cookie 容器在连接层），"
                + "请在创建 ConnectionPoolOptions 时设置 UseCookies / Credentials，"
                + "或不传 pool 让本实例自建池。", config);
        }

        if (defaults.Proxy is not null || defaults.Credentials is not null)
        {
            throw AxiosException.InvalidOption(
                "共享连接池时 Proxy / Credentials 由池决定，"
                + "请在 ConnectionPoolOptions.Proxy / Credentials 上设置，或不传 pool 让本实例自建池。", config);
        }
    }

    // ------------------------------------------------------------------
    // getUri：只组装 URL，不发请求（对应 axios.getUri）
    // ------------------------------------------------------------------

    /// <summary>
    /// 按配置拼出最终请求 URL（含 baseURL 合并与 params 序列化），不发请求。
    /// 返回的是实际发送形态（已转义，如空格 → <c>%20</c>），可用于签名、日志与调试。
    /// <para>
    /// 注意：<see cref="System.Uri"/> 会按 RFC 3986 归一化百分号编码的非保留字符
    /// （<c>%41</c>→<c>A</c>、<c>%7E</c>→<c>~</c>），这与请求行实际发送的内容一致；
    /// 需要按原样保留调用方传入的编码串时，请以本方法的返回值作为签名输入。
    /// </para>
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
        return SendAsync(config, dataType: null);
    }

    /// <summary>对应 axios(config)，Data 反序列化为 T。</summary>
    public Task<AxiosResponse<T>> RequestAsync<T>(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SendTypedAsync<T>(config);
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

    // ------------------------------------------------------------------
    // 核心请求管线
    // ------------------------------------------------------------------

    private const string DisposedMessage = "AxiosClient 已释放，不能发起新请求";

    /// <summary>
    /// 请求入口：负责「已释放」拦截、在途计数（配合 <see cref="Dispose"/> 的推迟释放协议）与配置合并，
    /// 校验通过后转入 <see cref="SendCoreAsync"/>。
    /// </summary>
    private async Task<AxiosResponse> SendAsync(AxiosConfig perCallConfig, Type? dataType)
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

        // 先占位再复核：保证请求要么完整跑在释放之前，要么明确收到错误，
        // 不会出现「拿着已释放的 Handler/池资源跑到一半」的中间态。
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

        // 生效配置：快捷方法传进来的已经是合并好的（见 Shortcut），其余入口在这里合并到实例默认值。
        // 校验一律对「生效配置」做：两种来源的判定条件相同（见 ValidatePerRequestOptions）。
        AxiosConfig config;
        try
        {
            config = perCallConfig.EffectiveMerged ? perCallConfig : MergeConfig(perCallConfig);
            ValidatePerRequestOptions(config);
        }
        catch
        {
            ReleaseInFlight();
            throw;
        }

        try
        {
            return await SendCoreAsync(config, dataType).ConfigureAwait(false);
        }
        finally
        {
            ReleaseInFlight();
        }
    }

    /// <summary>最后一个在途请求结束时，补做 <see cref="Dispose"/> 推迟的资源释放。</summary>
    private void ReleaseInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) == 1)
        {
            DisposeResources();
        }
    }

    private async Task<AxiosResponse> SendCoreAsync(AxiosConfig config, Type? dataType)
    {
        // 泛型调用 XxxAsync<dynamic>/<object> 时（T 即 object），响应自动展开为动态对象图，
        // 对标 JS 中直接 response.data.xxx 的用法；显式指定过其他 responseType 时以显式配置为准。
        if (dataType == typeof(object) && config.ResponseType == ResponseType.Json)
        {
            config.ResponseType = ResponseType.Dynamic;
        }

        // 泛型调用 XxxAsync<Stream> 时自动切换为流式响应（对标 axios responseType: 'stream'），
        // 下载大文件直接用，不必再手动设置 ResponseType。显式配置的 responseType 仍然优先。
        if (dataType == typeof(Stream) && config.ResponseType == ResponseType.Json)
        {
            config.ResponseType = ResponseType.Stream;
        }

        // 负值没有任何合理语义，静默当作「不限制」会让调用方以为限制生效了 —— 直接报错。
        ValidateRangeConfig(config);

        // 1) 请求拦截器：与 axios 一致，按注册逆序执行
        foreach (var interceptor in Interceptors.Request.SnapshotReversed())
        {
            AxiosConfig next;
            try
            {
                next = await interceptor.OnFulfilled(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (interceptor.OnRejected is null)
                {
                    throw;
                }

                next = await interceptor.OnRejected(ex).ConfigureAwait(false);
            }

            // 返回 null 时此前会在下一行（config.TransformRequest）炸成裸 NullReferenceException，
            // 调用方既拿不到 AxiosException 也看不出是哪一层拦截器出的错。
            config = next ?? throw AxiosException.InvalidOption(
                "请求拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosConfig 实例"
                + "（要做修改请改传入的 config 后端返回它）。",
                config);
        }

        // 2) transformRequest：拦截器之后、编码之前依次作用于数据（对应 axios transformRequest）
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
        // 所以从这里开始到方法结束的所有路径（含 CancelAfter 越界、请求体超限、拦截器抛异常）
        // 都必须先释放它；只把「发送 + 读取」包进 finally 会把未发出的请求体漏掉。
        try
        {
            HttpContent? content;
            try
            {
                content = RequestBodyFactory.Create(config.Data, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // 序列化失败（循环引用、自定义转换器抛错、multipart 缺字段名等）同样走统一错误模型；
                // 取消类异常原样抛出，保证 IsCancel 语义不被吞掉。
                throw AxiosException.BadRequestPayload(
                    config,
                    request,
                    config.Data?.GetType().Name ?? "null",
                    ex);
            }

            // 上传进度 / 限速：包一层流量统计（头部从原内容复制，序列化逻辑不变）
            if (content is not null && (config.OnUploadProgress is not null || config.MaxRate is > 0))
            {
                content = new ProgressHttpContent(content, config.OnUploadProgress, config.MaxRate);
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

            // 只有「设了超时」或「外部令牌可取消」才需要链接源：两者都不成立时 linkedCts.Token
            // 恒等于 None，为每个请求建一个 CTS（连同 SendAsync 里的令牌注册）换不来任何行为差异。
            using CancellationTokenSource? linkedCts = hasTimeout || externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : null;

            if (hasTimeout)
            {
                // CancelAfter 的上限是 uint.MaxValue-1 毫秒（约 49.7 天），越界会抛裸
                // ArgumentOutOfRangeException：收敛到上限，语义等价于「极长但有限」。
                linkedCts!.CancelAfter(Timeouts.Clamp(config.Timeout));
            }

            // 交给底层每一次 I/O 的令牌：无超时且无外部取消时就是 None（不注册、不分配）。
            var sendToken = linkedCts?.Token ?? CancellationToken.None;

            var streamResponse = config.ResponseType == ResponseType.Stream;

            // 流式响应的 timeout 需要延续到正文读取结束，这里记录请求起点用于计算剩余时间。
            // 只有「流式 + 设了超时」用得上：其余组合下 remaining 恒为 null，计时器纯属白分配。
            var clock = streamResponse && hasTimeout ? System.Diagnostics.Stopwatch.StartNew() : null;

            // 流式正文的所有权交给调用方持有的流；拦截器抛异常或丢弃响应时由这里兜底释放，
            // 否则租约永远不归还，该 origin 节点再也不会被驱逐。
            IDisposable? streamOwner = null;

            // 4) 真正的发送 + 读取 + 状态码校验（异常会进入响应拦截器 rejected 链）
            Func<Task<AxiosResponse>> invoker = async () =>
            {
                PoolLease? lease = null;
                try
                {
                    // 注入 Handler 的实例：固定 HttpClient，所有 origin 共用；
                    // 默认实例：按 origin 从连接池租用（连接复用 + 并发上限）。
                    // 租用也走异步入口并把链接后的令牌传进去：已取消/已超时的请求在租用前就会被截断，
                    // 抛出的 OperationCanceledException 与发送阶段同规则翻译成统一的错误模型。
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
                        // 状态码的接受性只判定一次：它同时决定「宽松解析」与「是否抛错」，
                        // 调用两次会让带副作用的 validateStatus（计数、日志、限流）被执行两遍，
                        // 且两次结果不一致时（非确定性判定）行为自相矛盾。
                        var validateStatus = config.ValidateStatus ?? DefaultValidateStatus;
                        statusAccepted = validateStatus((int)responseMessage.StatusCode);

                        // 状态码不合法时转换必须宽松：否则 404 + HTML 错误页会在反序列化处抛错，
                        // 把本该带 Status/Response 的 ERR_BAD_REQUEST 变成 ERR_BAD_RESPONSE 且两者全丢。
                        response = await ReadResponseAsync(
                                responseMessage,
                                request,
                                config,
                                sendToken,
                                dataType,
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
                        // 连接中途断开 / 响应体被截断（HttpIOException）：统一成 ERR_NETWORK，而不是裸 IOException。
                        responseMessage.Dispose();
                        throw AxiosException.Network(config, request, ex);
                    }
                    catch
                    {
                        // 读取/解析失败（含 maxContentLength 超限、反序列化失败）：先释放响应，避免连接泄漏
                        responseMessage.Dispose();
                        throw;
                    }

                    // stream 模式下消息不能释放（调用方要继续读流），所有权转移给 ResponseStreamOwner。
                    if (!streamResponse)
                    {
                        responseMessage.Dispose();
                    }

                    if (!statusAccepted)
                    {
                        if (streamResponse)
                        {
                            // 失败响应没有人会去读正文：立即释放内容把连接还回池里，
                            // 否则这条连接会被一个永远不会被读取的流一直占住（重试几次就能打满连接池）。
                            (response.Data as IDisposable)?.Dispose();
                            response.Data = null;
                            responseMessage.Dispose();
                        }

                        throw AxiosException.FromResponse(config, request, response);
                    }

                    if (streamResponse && response.Data is Stream body)
                    {
                        // 无条件包装（Timeout=0 也包）：只有这样「外部取消」与「正文阶段 I/O 异常」
                        // 在流式响应上才和非流式一致，否则同一场景会因 Timeout 配置不同给出不同的异常类型。
                        var remaining = clock is not null
                            ? config.Timeout - clock.Elapsed
                            : (TimeSpan?)null;

                        Stream wrapped = new TimeoutStream(body, remaining, externalToken, config, request);

                        // 租约 + HttpResponseMessage 的所有权一并转移给调用方持有的流：
                        // 下载期间该 origin 仍被视为活跃（避免空闲驱逐把连接回收），
                        // 流 Dispose 时释放响应消息（此前流式路径只释放正文，消息本体永久悬空）。
                        wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease);
                        lease = null;

                        response.Data = wrapped;
                        streamOwner = wrapped;
                    }

                    return response;
                }
                catch (ObjectDisposedException ex)
                {
                    // 池被直接 Dispose()/Clear()（或在途时关闭）：底层 Handler/Client 已释放，
                    // 裸 ObjectDisposedException 对调用方没有信息量，统一翻译成「请求被中止」。
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

                        // 返回 null 此前会被当成「响应就是 null」原样交回调用方，
                        // GetAsync<T> 随后在 AsTyped 上炸 NRE —— 明确报成配置错误。
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

            // 拦截器返回了新实例：原流式正文没有任何引用，需要在这里释放。
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

    private static bool DefaultValidateStatus(int status) => status is >= 200 and < 300;

    /// <summary>
    /// 响应拦截器返回 <c>null</c>：返回类型是非空的 <see cref="AxiosResponse"/>，
    /// 静默把 null 交出去会让调用方在很远的地方炸 NRE（<c>GetAsync&lt;T&gt;</c> 的 <c>AsTyped</c>）。
    /// </summary>
    private static AxiosException NullInterceptorResult(AxiosConfig config)
        => AxiosException.InvalidOption(
            "响应拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosResponse 实例"
            + "（要中断这次请求请抛异常，不要返回 null）。",
            config);

    /// <summary>
    /// 限制类配置的取值范围校验。
    /// <para>
    /// 负值此前一律被静默当作「不限制」（<c>Timeout &gt; 0</c> / <c>is &gt; 0</c> 判断），
    /// 调用方会以为限制生效了。这里改成显式报错，语义边界写清楚：
    /// <c>Timeout = 0</c> 表示不超时；<c>MaxRate = 0</c> 等价于不限速；<c>null</c> 表示不限。
    /// </para>
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
    /// 实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造连接池时已固定，
    /// 按请求写<b>不同的值</b>无法生效 —— 与其静默忽略，不如明确报错；
    /// 写<b>与实例生效值相同</b>的值则是等价写法，放行（否则调用方把 defaults 抄一遍到每个请求上都会炸）。
    /// <para>
    /// 判定对象是<b>合并后的生效配置</b>：快捷方法把请求项直接写进 defaults 的副本，非快捷入口在
    /// <see cref="SendAsync"/> 里合并，两者到这里的形态一致 —— 于是只需判定「值与实例生效值是否相同」，
    /// 不用再靠 <c>*Set</c> 标记去追「是谁写的」。
    /// </para>
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
    /// <c>WithCredentials</c>：只允许复述实例生效值。
    /// <para>
    /// 判定依据是「值是否与生效值不同」而不是「是否设置过」：写相同的值完全等价于不写，没有理由报错。
    /// 注入自定义 Handler 时无从得知它的 Cookie 策略、无法判断是否等价，只要写就报错
    /// （这种实例的 defaults 上不可能设过该项，构造期就拦了，因此 <c>WithCredentialsSet</c> 为 true 一定是本次写的）。
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
    /// <c>MaxRedirects</c>：只允许写与实例生效值相同的值。
    /// <para>
    /// 与实例默认值相同就没什么可判的；不同的值再与池上真正生效的策略比一次
    /// ——「defaults 没设置、poolOptions 把上限改成 3、按请求写 3」正是这种等价写法。
    /// 生效值取池的实际策略（<c>AllowAutoRedirect = false</c> 时视为 0），不是属性默认值 5。
    /// </para>
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

        if (config.MaxRedirects == Defaults.MaxRedirects)
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
    /// 下载侧包装：先按 Content-Length 做 maxContentLength 预检，
    /// 再按需套上进度/限速/长度上限流（三者都不需要时原样返回）。
    /// <para>
    /// 长度上限必须在流上生效，而不只是非流式路径的「边读边计数」：流式响应（含 chunked、
    /// 服务端未声明长度）此前完全没有 maxContentLength 约束，调用方以为设了限制其实没设。
    /// </para>
    /// </summary>
    private static Stream WrapDownloadStream(
        Stream stream,
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config)
    {
        var declared = message.Content.Headers.ContentLength;

        if (config.MaxContentLength is long limit && declared is long length && length > limit)
        {
            stream.Dispose();
            throw AxiosException.MaxContentLength(config, request, length, limit);
        }

        if (config.OnDownloadProgress is null && config.MaxRate is not > 0 && config.MaxContentLength is null)
        {
            return stream;
        }

        return new ProgressReadStream(
            stream,
            declared,
            config.OnDownloadProgress,
            config.MaxRate,
            config.MaxContentLength,
            config,
            request);
    }

    /// <summary>读取整个响应体；服务端未声明长度时按已读字节执行 maxContentLength。</summary>
    private static async Task<byte[]> ReadAllBytesAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        long? declaredLength)
    {
        const int chunkSize = 81920;

        // 声明了长度时按声明值预分配，避免 MemoryStream 反复扩容（大响应在 LOH 上抖动明显）。
        // 上限 8MB：防止恶意 Content-Length 直接触发一次超大分配。
        const long maxPreallocated = 8 * 1024 * 1024;
        var capacity = declaredLength is > 0 and <= maxPreallocated ? (int)declaredLength.Value : 0;
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            using var body = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (config.MaxContentLength is long limit && body.Length + read > limit)
                {
                    throw AxiosException.MaxContentLength(config, request, body.Length + read, limit);
                }

                body.Write(buffer, 0, read);
            }

            // 「精确预分配且刚好写满」时底层数组没有多余空间，直接交出，省掉 ToArray 的第二次拷贝
            // （大响应在 LOH 上每少一次整段复制都很可观）。注意必须同时确认底层数组长度 == 已读字节：
            // ArraySegment.Array 是完整底层数组（容量可能大于 Count），只比 Count == Length 会把
            // 扩容路径（未声明长度等）的零填充一起交出去。任何一项不吻合就退回拷贝。
            return body.TryGetBuffer(out var segment)
                && segment.Count == body.Length
                && segment.Array is { Length: var arrayLength }
                && arrayLength == segment.Count
                ? segment.Array
                : body.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Dispose();
        }
    }

    private static async Task<AxiosResponse> ReadResponseAsync(
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config,
        CancellationToken cancellationToken,
        Type? dataType,
        bool lenient)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
        {
            headers[header.Key] = header.Value;
        }

        foreach (var header in message.Content.Headers)
        {
            headers[header.Key] = header.Value;
        }

        var status = (int)message.StatusCode;
        var statusText = StatusTextMap.Get(status);

        // 反序列化 / transformResponse 阶段可读的响应上下文：零拷贝复用刚建好的 headers。
        // 让序列化器能按响应头决定解析规则（如 X-Schema-Version），transformResponse 能搬元数据
        // （如 X-Total-Count）。响应体的文本/二进制判定不受影响 —— 那由 MediaTypes.IsTextual 决定。
        var contentType = message.Content.Headers.ContentType;
        config.Response = new AxiosResponseInfo(
            status,
            statusText,
            headers,
            contentType?.MediaType,
            contentType?.CharSet);

        object? data;
        if (config.ResponseType == ResponseType.Stream)
        {
            var responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            data = WrapDownloadStream(responseStream, message, request, config);
        }
        else
        {
            // 原始内容优先：一律先取原始字节，再按需解码为文本，最后按返回类型转换。
            Stream responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var downloadStream = WrapDownloadStream(responseStream, message, request, config);
            var declared = message.Content.Headers.ContentLength;
            var bytes = await ReadAllBytesAsync(downloadStream, config, request, cancellationToken, declared)
                .ConfigureAwait(false);

            var charSet = contentType?.CharSet;
            var responseMediaType = contentType?.MediaType;

            // 响应体是文本还是二进制，由 Content-Type 决定（无 Content-Type 视为文本）。
            // 二进制响应连解码都不做：protobuf / 图片 / 下载流没理由先转成 UTF-16 再交给序列化器。
            var textual = MediaTypes.IsTextual(responseMediaType);

            // 只要字节的调用（arraybuffer / byte[] / 二进制响应的业务对象）不做文本解码。
            // 其余情况只解码一次，文本在 ConvertData 里按需使用（基础类型直读、文本对象交序列化器）。
            var text = NeedsText(config, dataType, textual)
                ? ResponseTextDecoder.Decode(bytes, charSet, config.ResponseEncoding)
                : null;
            data = ConvertData(config, request, dataType, bytes, text, charSet, textual, responseMediaType, lenient);

            // transformResponse：在反序列化之后依次加工 Data（对应 axios transformResponse）
            if (config.HasTransformResponse)
            {
                foreach (var transform in config.TransformResponse)
                {
                    data = transform(data, config);
                }
            }
        }

        return new AxiosResponse
        {
            Data = data,
            Status = status,
            StatusText = statusText,
            Headers = headers,
            Config = config,
            Request = request
        };
    }

    /// <summary>
    /// 是否真的需要把响应字节解码成文本。
    /// arraybuffer / stream / byte[] 只给原始字节；二进制响应（Content-Type 非文本）的业务对象走
    /// 序列化器的字节入口，同样不解码；其余形态（text、dynamic、基础类型、文本对象）都需要文本
    /// —— 解码一次，后续路径共用。
    /// </summary>
    private static bool NeedsText(AxiosConfig config, Type? dataType, bool textual) => config.ResponseType switch
    {
        ResponseType.ArrayBuffer => false,
        ResponseType.Stream => false,
        ResponseType.Text or ResponseType.Dynamic => true,
        _ => textual && dataType != typeof(byte[])
    };

    /// <summary>body 是否只含空白字符（<c>""</c> 与 <c>"  \r\n"</c> 语义等价）。</summary>
    private static bool IsBlank(string text) => text.AsSpan().Trim().IsEmpty;

    /// <summary>
    /// 尚未解码时的空白判定：只含 ASCII 空白（SP/HTAB/CR/LF）的字节串无需解码就能判空。
    /// 非 ASCII 空白（如 NBSP）留给解码后的 <see cref="IsBlank"/> 兜底。
    /// </summary>
    private static bool IsBlankUtf8(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把「原始字节 + 已解码文本」按 responseType / 目标类型转换为 Data。
    /// <para>
    /// 分派原则（与格式解耦）：原始字节、文本、dynamic 图、基础数据类型都由框架自己给出；
    /// 只有<b>业务对象</b>才交给 <see cref="IContentSerializer"/>，且按 <paramref name="textual"/>
    /// 决定进文本入口还是字节入口。
    /// </para>
    /// <para><paramref name="lenient"/> 为 true 时（状态码不合法）解析失败返回原始内容，不抛异常。</para>
    /// </summary>
    private static object? ConvertData(
        AxiosConfig config,
        HttpRequestMessage request,
        Type? dataType,
        byte[] bytes,
        string? text,
        string? charSet,
        bool textual,
        string? mediaType,
        bool lenient)
    {
        // 按需解码：只要字节的分支永远不付 bytes → UTF-16 的代价。
        string Text() => text ??= ResponseTextDecoder.Decode(bytes, charSet, config.ResponseEncoding);

        // 1) 显式 responseType 优先：arraybuffer 直接给原始字节。
        if (config.ResponseType == ResponseType.ArrayBuffer)
        {
            return bytes;
        }

        // 2) text 直接给原始文本（不做任何解析）。
        if (config.ResponseType == ResponseType.Text)
        {
            return Text();
        }

        // 3) dynamic 展开：JSON 特有的「无类型对象图」，由框架解析，不经过序列化器。
        if (config.ResponseType == ResponseType.Dynamic)
        {
            var json = Text();
            if (IsBlank(json))
            {
                return null;
            }

            try
            {
                return DynamicJson.Parse(json);
            }
            catch (Exception ex) when (ex is not AxiosException)
            {
                // 非 JSON 响应（HTML 错误页、纯文本等）同样走统一错误模型，而不是裸解析异常。
                // 这是**有意比 axios 严格**：dynamic 的典型用法是 data.user.name，
                // 把一段 HTML 当 data 交回去只会在几十行之后炸成 RuntimeBinderException。
                // 状态码不合法时（lenient）仍然回退原文。
                if (lenient)
                {
                    return json;
                }

                throw AxiosException.BadResponse(config, request, "dynamic JSON", ex);
            }
        }

        // 4) 目标类型就是原始内容本身：string 原样返回（支持纯文本），byte[] 直接给字节。
        if (dataType == typeof(string))
        {
            return Text();
        }

        if (dataType == typeof(byte[]))
        {
            return bytes;
        }

        // 5) 空 body 视为 null；只含空白的 body 同样视为空 —— 否则服务端多吐一个换行就会让
        //    typed 请求报 ERR_BAD_RESPONSE，而空 body 却返回 null，两者本该等价。
        if (text is null ? IsBlankUtf8(bytes) : IsBlank(text))
        {
            return null;
        }

        // 6) 没有目标类型（非泛型 / JsonElement）：框架按 JSON 文档形态给出，
        //    解析失败原样返回文本（对齐 axios transformResponse）。
        //    注意这里不用序列化器：它已经没有「文档形态」的概念，而「无类型」本来也不需要业务反序列化。
        if (dataType is null || dataType == typeof(object) || dataType == typeof(JsonElement))
        {
            // 二进制响应没有「文档形态」，原样给字节——把 protobuf / 图片当文本解析只会得到乱码。
            if (!textual)
            {
                return bytes;
            }

            var document = Text();
            try
            {
                using var parsed = JsonDocument.Parse(document);
                return parsed.RootElement.Clone();
            }
            catch (Exception ex) when (ex is not AxiosException)
            {
                return document;
            }
        }

        // 7) 二进制响应（Content-Type 声明为非文本）：不做文本解码，交给序列化器的字节入口。
        //    Protobuf / MessagePack / CBOR 因此拿到的就是原始字节；文本格式的序列化器不用实现该入口
        //    （接口里默认按 UTF-8 解码转调文本入口）。
        if (!textual)
        {
            try
            {
                return AxiosContent.Get(config, mediaType).Deserialize(bytes, dataType, config);
            }
            catch (Exception ex) when (ex is not AxiosException)
            {
                // lenient（状态码不合法）：解析失败不抛，原样回字节——此时已经没有「文本」可回退。
                if (lenient)
                {
                    return bytes;
                }

                throw AxiosException.BadResponse(config, request, dataType, ex);
            }
        }

        var raw = Text();

        // 8) 基础数据类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）：框架直接解析文本，
        //    不依赖序列化器 —— XML 的 <Age>30</Age> 与 JSON 的裸 42 都走这里。
        //    JSON 的带引号形态（"42"）框架解析不了，交给下面的序列化器兜底。
        if (ScalarParser.TryParse(raw, dataType, out var scalar))
        {
            return scalar;
        }

        // 9) 文本响应的业务对象：交给当前序列化器，返回什么对象，Data 就是什么。
        try
        {
            return AxiosContent.Get(config, mediaType).Deserialize(raw, dataType, config);
        }
        catch (Exception ex) when (ex is not AxiosException)
        {
            if (lenient)
            {
                return raw;
            }

            throw AxiosException.BadResponse(config, request, dataType, ex);
        }
    }

    /// <summary>
    /// 释放实例。与共享连接池一致的优雅语义：<b>不打断在途请求</b>——
    /// 仍有在途请求时，Handler/池的释放推迟到最后一个请求结束（由它代为执行），
    /// 但此后不再接受新请求（抛 <c>ERR_CANCELED</c>）。
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
    /// 与 <see cref="Dispose"/> 等价；连接池的关闭本身是同步的（无待刷写缓冲），
    /// 这里实现它只是为了让调用方可以 <c>await using</c>。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
