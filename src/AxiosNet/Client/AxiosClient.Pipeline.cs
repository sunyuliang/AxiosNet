using System.Diagnostics;
using System.Runtime.CompilerServices;
using AxiosNet.Cache;
using AxiosNet.Interceptors;
using AxiosNet.Metadata;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的请求管线：合并 → 拦截 → 规范化 → 编码，以及一次请求的成型（URL / 协议 / 正文 / 请求头）。
public sealed partial class AxiosClient
{
    /// <summary>请求入口：按固定顺序驱动整条管线 —— 合并 → 校验（实例级）→ 拦截 → 规范化 / 范围校验 →（短路响应 | 编码 → 缓存 → 尝试管线 → 落库）→ 收尾。</summary>
    /// <remarks>公开入口返回真正的 <see cref="Task{TResult}"/>（可多次 await），这一步只做适配。</remarks>
    private Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
        => SendAsyncCore(perCallConfig, dataType, target).AsTask();

    /// <summary>管线核心：与 <see cref="SendAsync"/> 同一流程，状态机用池化 builder。</summary>
    /// <remarks>返回的 <c>ValueTask</c> 只能消费一次：公开入口用 <c>AsTask()</c> 交给调用方，泛型入口直接 await。</remarks>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<AxiosResponse> SendAsyncCore(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // 阶段 0：守卫（在 try 之外）。
        EnterInFlight(perCallConfig);

        // 总预算计时起点：调用入口。
        var entryTimestamp = Stopwatch.GetTimestamp();

        // 遥测状态全是局部量。
        AxiosConfig? config = null;
        Uri? telemetryUri = null;

        // 本次请求认下的观测实现。
        GuardedTelemetrySink? telemetry = null;
        var telemetryEntered = false;
        var telemetryStart = 0L;
        AxiosRequestTrace telemetryTrace = default;

        // 观测收尾（ExitRequest → span 收尾）打包成一次 Dispose。
        TelemetryScope telemetryScope = default;

        // 缓存状态。
        AxiosCache? cache = null;
        string? cacheKey = null;
        AxiosCacheFlight? flight = null;
        CacheRequestDirectives cacheDirectives = CacheRequestDirectives.None;
        var cacheMissed = false;

        try
        {
            // 阶段 1：合并 —— 得到本次请求的生效配置。
            config = MergeEffectiveConfig(perCallConfig);

            // 阶段 2：校验（实例级选项），判在拦截器之前。
            ValidatePerRequestOptions(config);

            // 阶段 2.5：凭据 —— 在拦截器之前解析成 Authorization 头值。
            var authBeforeIntercept = config.TransportOrNull?.Auth;
            var resolvedBeforeIntercept = config.ResolvedAuthorization;
            var explicitHeaderBeforeResolve = HasAuthorizationHeader(config);
            await ResolveCredentialAsync(config).ConfigureAwait(false);

            // 提供方自己写的头不算冲突。
            var headerFromProvider = HasAuthorizationHeader(config);

            // 阶段 3：拦截 —— 请求拦截器按注册逆序执行（与 axios 一致）。
            config = await InterceptRequestAsync(config).ConfigureAwait(false);

            // 阶段 3.5：拦截器可能换掉配置或头，冲突校验与解析补到之后。
            if (explicitHeaderBeforeResolve || (HasAuthorizationHeader(config) && !headerFromProvider))
            {
                ValidateCredentialConflict(config);
            }

            if (ReferenceEquals(authBeforeIntercept, config.TransportOrNull?.Auth))
            {
                // 槽位没变：沿用拦截器前的解析结果。
                if (config.ResolvedAuthorization is null && resolvedBeforeIntercept is not null)
                {
                    config.ResolvedAuthorization = resolvedBeforeIntercept;
                }
            }
            else
            {
                // 槽位被换掉 / 清空：重新解析。
                await ResolveCredentialAsync(config).ConfigureAwait(false);
            }

            // 阶段 4a ~ 4d 使用拦截器之后的配置。
            ApplyAutoResponseType(config, dataType);

            // 范围校验与是否发出无关，放在短路之前。
            ValidateRangeConfig(config);

            // 闸门与编码只对真实 HTTP 操作有意义。
            if (config.ShortCircuit is { } shortCircuit)
            {
                return await CompleteShortCircuitAsync(shortCircuit, config, dataType, target).ConfigureAwait(false);
            }

            var gate = ResolveConcurrencyGate();

            // 总预算换成绝对截止时间戳。
            var deadlineTimestamp = TotalTimeoutBudget.Start(config, entryTimestamp);

            // 阶段 5：编码 —— transformRequest → URL / 协议 → 正文 / 请求头。
            ApplyRequestTransforms(config);
            var plan = BuildRequestPlan(config);

            // 阶段 5.5：缓存 —— 命中不发请求；未命中时同键并发只发一次。
            if (config.Caching is { } configuredCache && configuredCache.CanHandle(config))
            {
                cache = configuredCache;
                cacheKey = configuredCache.KeyFor(config, plan.Uri);

                // 请求侧 Cache-Control / Pragma：只有非 Configured 模式才解析。
                cacheDirectives = configuredCache.ReadRequestDirectives(config);

                // 请求要求绕开缓存时不查、不参与单飞，但仍会尝试落库。
                if (!cacheDirectives.SkipLookup)
                {
                    if (await configuredCache.TryGetAsync(cacheKey, config, deadlineTimestamp).ConfigureAwait(false) is { } cachedReply)
                    {
                        AxiosTelemetry.Dispatch.CacheHit(config);
                        return await CompleteShortCircuitAsync(cachedReply, config, dataType, target).ConfigureAwait(false);
                    }

                    cacheMissed = true;

                    if (configuredCache.SingleFlight)
                    {
                        flight = configuredCache.Join(cacheKey);

                        if (!flight.IsLeader)
                        {
                            try
                            {
                                var sharedReply = await AwaitFlightAsync(flight, config, deadlineTimestamp).ConfigureAwait(false);

                                if (sharedReply is not null)
                                {
                                    AxiosTelemetry.Dispatch.CacheHit(config);
                                    cacheMissed = false;
                                    return await CompleteShortCircuitAsync(sharedReply, config, dataType, target).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                // 跟随者不落库、不参与广播。
                                flight = null;
                            }
                        }
                    }
                }

                // 查过没有且真要发出去：计一次未命中。
                if (cacheMissed)
                {
                    AxiosTelemetry.Dispatch.CacheMiss(config);
                }

                // 请求侧否决落库时不必为缓存复制正文。
                config.CacheCaptureRawBody = cacheDirectives.CanStore;

                // 条件请求：拿带校验器的旧条目，把校验器写成条件头。
                // no-store 不读缓存；调用方自己写了条件头时以调用方为准（不覆盖、也不接管 304）。
                if (configuredCache.ConditionalRequests
                    && !cacheDirectives.NoStore
                    && !HasCallerConditionalHeaders(config))
                {
                    var revalidation = await configuredCache
                        .TryGetRevalidationAsync(cacheKey, config, deadlineTimestamp)
                        .ConfigureAwait(false);

                    if (revalidation is not null && CacheValidators.HasAny(revalidation))
                    {
                        config.RevalidationEntry = revalidation;
                    }
                }
            }

            // 遥测起点：URL 拼好之后、真的进入发送阶段的请求。
            telemetryUri = plan.Uri;

            // 一次请求只认一个实现：判开 / 记进 / 开 span 用同一对象。
            telemetry = AxiosTelemetry.Dispatch;
            if (telemetry.IsEnabled(config))
            {
                telemetryStart = AxiosTelemetry.Timestamp;

                // 「已进」只在 EnterRequest 真的返回后置位。
                telemetryEntered = telemetry.TryEnterRequest();

                // 收尾快照先落定，后续任何一步抛异常都不能漏掉这次「出」。
                telemetryScope = new TelemetryScope(telemetry, telemetryEntered);

                if (telemetryEntered)
                {
                    telemetryTrace = telemetry.StartRequest(config, plan.Uri);
                    telemetryScope = telemetryScope.WithTrace(telemetryTrace);
                }
            }

            var template = CreateRequestMessage(config, plan, telemetryTrace);

            // 401 重放需要在第二次发送前继续持有正文。
            AxiosException? attemptFailure = null;

            // 模板持有请求体，所有路径都必须先释放它。
            try
            {
                // 阶段 6：发送 —— 响应拦截器链 + 尝试管线。
                var attempt = AxiosAttemptResult.Ensure(
                    await RunResponseInterceptorsAsync(
                        template,
                        AxiosAttemptContext.From(
                            config,
                            plan.Uri,
                            dataType,
                            target,
                            gate,
                            deadlineTimestamp,
                            ResolveFailoverHealth(config))).ConfigureAwait(false),
                    config);

                var result = attempt.Response;
                var streamOwner = attempt.StreamOwner;

                // 拦截器返回新实例时释放原流式正文。
                if (streamOwner is not null && !ReferenceEquals(result.Data, streamOwner))
                {
                    streamOwner.Dispose();
                }

                // 条件请求的 304：正文来自缓存，单独计数。
                if (result.CacheRevalidated)
                {
                    AxiosTelemetry.Dispatch.CacheRevalidated(config, telemetryTrace);
                }

                // 落库 + 放行跟随者，都在返回之前完成。
                if (cache is not null && cacheKey is not null)
                {
                    await StoreAsync(cache, cacheKey, flight, config, cacheDirectives, result, deadlineTimestamp).ConfigureAwait(false);
                    flight = null;
                }

                if (telemetryEntered)
                {
                    // 放在落库之后、template.Dispose() 之前。
                    telemetry!.RequestSucceeded(config, plan.Uri, result, telemetryStart, telemetryTrace);
                }

                return result;
            }
            catch (AxiosException error)
            {
                attemptFailure = error;
                throw;
            }
            finally
            {
                // 正文随模板一起释放；401 重放时交给失败的配置。
                if (config.RetainBodyContent
                    && attemptFailure is { Status: 401 }
                    && template.Content is not null
                    && attemptFailure.Config is { } failureConfig)
                {
                    failureConfig.RetainedReplayContent = template.Content;
                    template.Content = null;
                }

                template.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 领导者失败：HTTP 状态类失败把状态 / 头 / 原始正文共享给跟随者，各自判定；
            // 传输 / 取消类失败按约定广播。流式响应或拿不到原始字节时跟随者补发。
            if (flight is not null && cache is not null && cacheKey is not null)
            {
                if (ex is AxiosException { Response: not null } statusFailure && config is not null)
                {
                    cache.CompleteFlight(
                        cacheKey,
                        flight,
                        TryCreateSharedFailureReply(statusFailure.Response, config, cache));
                }
                else
                {
                    cache.FailFlight(cacheKey, flight, ex);
                }
            }

            // 失败路径不落库，内部缓冲只服务单飞共享。
            if (ex is AxiosException { Response: { } failedResponse })
            {
                failedResponse.RawBodyBuffer = null;
            }

            // 记录后原样抛出；观测异常已被派发层兜住。
            if (telemetryEntered)
            {
                telemetry!.RequestFailed(config, telemetryUri, ex, telemetryStart, telemetryTrace);
            }

            throw;
        }
        finally
        {
            // 条件请求的旧条目只属于这一次发送。
            if (config is not null)
            {
                config.RevalidationEntry = null;
            }

            // 收尾各段互不连坐，且都不把异常带给调用方。
            try
            {
                telemetryScope.Dispose();
            }
            finally
            {
                // 在途计数是 Dispose 的等待条件。
                ReleaseInFlight();
            }
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
                // null Task 就地报错（同步结果用 Task.FromResult）。
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

            // null 配置就地报错。
            config = next ?? throw AxiosException.InvalidOption(
                "请求拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosConfig 实例"
                + "（要做修改请改传入的 config 后端返回它）。",
                config);

            // 拦截器返回的配置未经阶段 1 合并时补一次。
            if (!next.EffectiveMerged && !next.InstanceOptionsInherited)
            {
                config = MergeConfig(next);
            }
        }

        return config;
    }

    /// <summary>
    /// 阶段 4a —— 规范化：泛型 <c>&lt;dynamic&gt;</c> / <c>&lt;object&gt;</c> 与 <c>&lt;Stream&gt;</c> 自动切换响应形态；判据是 <see cref="AxiosConfig.ResponseTypeSet"/>。
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
                config.Body = transform(config.Body, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                throw AxiosException.BadRequestPayload(config, null, "transformRequest", ex);
            }
        }
    }

    /// <summary>一次请求的成型结果：最终 URL 与协议协商参数（结构体）。</summary>
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

        /// <summary>请求的 HTTP 版本（现代资产默认 2.0，netstandard2.0 兼容资产默认 1.1）。</summary>
        public Version Version { get; }

        /// <summary>版本协商策略（默认 <see cref="HttpVersionPolicy.RequestVersionOrLower"/>）。</summary>
        public HttpVersionPolicy VersionPolicy { get; }
    }

    /// <summary>阶段 5b —— 成型：拼出最终 URL、确定协议协商参数，并做 HTTP/3 前置校验。</summary>
    /// <remarks>
    /// 默认 HTTP/2 + <see cref="HttpVersionPolicy.RequestVersionOrLower"/>（netstandard2.0 资产默认 1.1）；
    /// 强制 1.1 写 Version11，要 h3 写 Version30。
    /// </remarks>
    private static RequestPlan BuildRequestPlan(AxiosConfig config)
    {
        var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.QueryEncoder, config: config);

        var connection = config.TransportOrNull;
#if NETSTANDARD2_0
        // 兼容资产只能发 HTTP/1.1：默认版本是 1.1。
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version11;
#else
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version20;
#endif
        var versionPolicy = connection?.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower;

        // HTTP/3 前置校验：只在真的要 h3 时执行。
        ValidateHttp3Request(uri, httpVersion, versionPolicy, config);

        return new RequestPlan(uri, httpVersion, versionPolicy);
    }

    /// <summary>阶段 5c —— 编码：建请求模板（正文 + 请求头）；正文只准备一次，重试复用；成功返回时所有权交给调用方，内部失败自己释放。</summary>
    private static HttpRequestMessage CreateRequestMessage(
        AxiosConfig config,
        RequestPlan plan,
        AxiosRequestTrace telemetryTrace)
    {
        var template = new HttpRequestMessage(config.Method, plan.Uri)
        {
            Version = plan.Version,
#if !NETSTANDARD2_0
            VersionPolicy = plan.VersionPolicy
#endif
        };

        try
        {
            HttpContent? content;
            try
            {
                content = RequestBodyFactory.Create(config.Body, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // 序列化失败走统一错误模型，带来源名称。
                throw AxiosException.BadRequestPayload(
                    config,
                    template,
                    config.Body switch
                    {
                        FormData declared => declared.SourceName ?? declared.GetType().Name,
                        AggregateBody aggregate => aggregate.SourceName,
                        _ => config.Body?.GetType().Name ?? "null"
                    },
                    ex);
            }

            // 上传进度 / 限速：包一层流量统计（头部复制）。
            var callbacks = config.ObservabilityOrNull;
            var maxRate = config.TransportOrNull?.MaxRate;
            if (content is not null && (callbacks?.OnUploadProgress is not null || maxRate is > 0))
            {
                content = new ProgressHttpContent(content, callbacks?.OnUploadProgress, maxRate, config);
            }

            if (content is not null)
            {
                if (config.LimitsOrNull?.MaxRequestBytes is long maxBody
                    && content.Headers.ContentLength is long bodyLength
                    && bodyLength > maxBody)
                {
                    content.Dispose();
                    throw AxiosException.MaxRequestBytes(config, template, bodyLength, maxBody);
                }

                template.Content = content;

                // 长度已知时写进 span；句柄绕过派发层，要兜异常。
                if (telemetryTrace.IsActive)
                {
                    try
                    {
                        telemetryTrace.SetRequestBodySize(content.Headers.ContentLength);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                    }
                }
            }

            ApplyHeaders(template, config);
            ApplyConditionalHeaders(template, config);
            ApplyIdempotencyKey(template, config);
            return template;
        }
        catch
        {
            template.Dispose();
            throw;
        }
    }

    /// <summary>一次请求的观测收尾：<c>ExitRequest</c> 与 span 收尾按固定顺序执行。</summary>
    /// <remarks><c>sink</c> 是进入观测时的同一份快照；<c>trace</c> 自带派发，收尾回到创建它的实现。</remarks>
    private readonly struct TelemetryScope : IDisposable
    {
        private readonly GuardedTelemetrySink? _sink;
        private readonly bool _entered;
        private readonly AxiosRequestTrace _trace;

        /// <summary>只记录「认下哪个实现、是否已进入」；span 句柄随后由 <see cref="WithTrace"/> 补上。</summary>
        /// <param name="sink">本次请求认下的观测实现。</param>
        /// <param name="entered">是否成功进入观测（<c>ExitRequest</c> 只与它配对）。</param>
        public TelemetryScope(GuardedTelemetrySink? sink, bool entered)
        {
            _sink = sink;
            _entered = entered;
            _trace = default;
        }

        private TelemetryScope(GuardedTelemetrySink? sink, bool entered, AxiosRequestTrace trace)
        {
            _sink = sink;
            _entered = entered;
            _trace = trace;
        }

        /// <summary>补上本次请求的 span 句柄（未开 span 时为默认值）。</summary>
        /// <param name="trace">请求 span 句柄。</param>
        /// <returns>带上句柄的副本。</returns>
        public TelemetryScope WithTrace(AxiosRequestTrace trace) => new(_sink, _entered, trace);

        /// <summary>先退出观测、再收 span；span 收尾的异常被吞掉，退出观测的异常照常上抛。</summary>
        public void Dispose()
        {
            try
            {
                if (_entered)
                {
                    _sink!.ExitRequest();
                }
            }
            finally
            {
                try
                {
                    _trace.Stop();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 吞掉 span 收尾异常。
                }
            }
        }
    }
}
