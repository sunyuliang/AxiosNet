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

        AxiosConfig? config = null;
        Uri? telemetryUri = null;

        // 声明式装配失败改判器：属于「本次请求怎么来的」而不是「当前这份配置」，先抓下来。
        // 拦截器可以换掉整份配置（换过的配置不再携带它），改判不能因此失效。
        var constructionFailureMap = perCallConfig.ConstructionFailureMap;

        // 观测会话：进入 / 成功 / 失败 / 收尾都走它；未开观测时全是空操作。
        TelemetrySession telemetry = default;

        // 缓存状态。
        AxiosCache? cache = null;
        string? cacheKey = null;
        AxiosCacheFlight? flight = null;
        CacheRequestDirectives cacheDirectives = CacheRequestDirectives.None;

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

                var attempt = await TryServeFromCacheAsync(cache, config, plan.Uri, deadlineTimestamp, dataType, target)
                    .ConfigureAwait(false);

                cacheKey = attempt.Key;
                flight = attempt.Flight;
                cacheDirectives = attempt.Directives;

                if (attempt.Served is { } served)
                {
                    return served;
                }
            }

            // 遥测起点：URL 拼好之后、真的进入发送阶段的请求。
            telemetryUri = plan.Uri;

            // 一次请求只认一个实现：判开 / 记进 / 开 span 用同一对象。
            telemetry = TelemetrySession.Enter(config, telemetryUri);

            var template = CreateRequestMessage(config, plan, telemetry.Trace);

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
                    AxiosTelemetry.Dispatch.CacheRevalidated(config, telemetry.Trace);
                }

                // 落库 + 放行跟随者，都在返回之前完成。
                if (cache is not null && cacheKey is not null)
                {
                    await StoreAsync(cache, cacheKey, flight, config, cacheDirectives, result, deadlineTimestamp).ConfigureAwait(false);
                    flight = null;
                }

                // 放在落库之后、template.Dispose() 之前。
                telemetry.RequestSucceeded(config, plan.Uri, result);

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
                HandoffBodyForReplay(config, template, attemptFailure);
                template.Dispose();
            }
        }
        catch (Exception ex)
        {
            BroadcastFlightFailure(cache, cacheKey, flight, config, ex);

            // 失败路径不落库，内部缓冲只服务单飞共享。
            if (ex is AxiosException { Response: { } failedResponse })
            {
                failedResponse.RawBodyBuffer = null;
            }

            // 记录后原样抛出；观测异常已被派发层兜住。
            telemetry.RequestFailed(config, telemetryUri, ex);

            // 声明式入口的装配失败改判：原始异常仍是 Cause，对外错误码由声明方决定（见 ConstructionFailureMap）。
            // 本次请求的改判器优先，异常自身带的配置只做兜底。
            if (ex is AxiosException { IsRequestConstructionFailure: true } constructionFailure
                && (constructionFailureMap ?? constructionFailure.Config?.ConstructionFailureMap) is { } map)
            {
                throw map(constructionFailure);
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
                telemetry.Dispose();
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
    /// 阶段 4a —— 规范化：先拒掉 <see cref="HttpContent"/> 这一类的请求侧形态，再把泛型 <c>&lt;dynamic&gt;</c> / <c>&lt;object&gt;</c> 与 <c>&lt;Stream&gt;</c> 自动切换响应形态（判据是 <see cref="AxiosConfig.ResponseTypeSet"/>）。
    /// </summary>
    private static void ApplyAutoResponseType(AxiosConfig config, Type? dataType)
    {
        // HttpContent 是请求侧「原始内容」形态，响应方向没有对应物：在发出之前就按选项错误拒掉，免得白白发一次请求再报反序列化失败。
        if (dataType is not null && typeof(HttpContent).IsAssignableFrom(dataType))
        {
            throw AxiosException.InvalidOption(
                $"泛型参数 {dataType.Name} 不能当响应形态：HttpContent 是请求侧「原始内容」的写法。"
                + "要响应字节用 GetAsync<byte[]>（或 ResponseType.ArrayBuffer），要网络流用 GetAsync<Stream>（或 ResponseType.Stream），"
                + "要原文用 GetAsync<string>（或 ResponseType.Text）。",
                config);
        }

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

                // 长度已知时写进 span（句柄经派发层转发，异常边界在护栏）。
                if (telemetryTrace.IsActive)
                {
                    telemetryTrace.SetRequestBodySize(content.Headers.ContentLength);
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

    /// <summary>401 且开了正文保留时，把模板里的正文交给失败配置，供重放复用。</summary>
    /// <param name="config">本次请求的生效配置（<c>RetainBodyContent</c> 取自这里）。</param>
    /// <param name="template">持有正文的请求模板。</param>
    /// <param name="failure">本次失败的异常；只有 401 状态失败才交接。</param>
    private static void HandoffBodyForReplay(AxiosConfig config, HttpRequestMessage template, AxiosException? failure)
    {
        if (config.RetainBodyContent
            && failure is { Status: 401 }
            && template.Content is not null
            && failure.Config is { } failureConfig)
        {
            failureConfig.RetainedReplayContent = template.Content;
            template.Content = null;
        }
    }

    /// <summary>一次请求的观测会话：进入、成功、失败、收尾各自封装，未开观测时全是空操作。</summary>
    /// <remarks>进入时快照一个实现，整条请求只认它；<c>ExitRequest</c> 与 span 停止的成对性由 <see cref="TelemetryScope"/> 保证。</remarks>
    private readonly struct TelemetrySession : IDisposable
    {
        private readonly GuardedTelemetrySink? _sink;
        private readonly TelemetryScope _scope;
        private readonly bool _entered;
        private readonly long _start;
        private readonly AxiosRequestTrace _trace;

        private TelemetrySession(
            GuardedTelemetrySink? sink,
            TelemetryScope scope,
            bool entered,
            long start,
            AxiosRequestTrace trace)
        {
            _sink = sink;
            _scope = scope;
            _entered = entered;
            _start = start;
            _trace = trace;
        }

        /// <summary>按配置判定是否观测，并完成进入与 span 开启；未开观测时返回默认值（各项操作皆为空）。</summary>
        /// <param name="config">本次请求的生效配置。</param>
        /// <param name="uri">最终 URL。</param>
        internal static TelemetrySession Enter(AxiosConfig config, Uri uri)
        {
            // 一次请求只认一个实现：判开 / 记进 / 开 span 用同一对象。
            var sink = AxiosTelemetry.Dispatch;
            if (!sink.IsEnabled(config))
            {
                return default;
            }

            var start = AxiosTelemetry.Timestamp;

            // 「已进」只在 EnterRequest 真的返回后置位。
            var entered = sink.TryEnterRequest();

            // 收尾快照先落定，后续任何一步抛异常都不能漏掉这次「出」。
            var scope = new TelemetryScope(sink, entered);

            if (!entered)
            {
                return new TelemetrySession(sink, scope, entered: false, start, default);
            }

            var trace = sink.StartRequest(config, uri);
            return new TelemetrySession(sink, scope.WithTrace(trace), entered: true, start, trace);
        }

        /// <summary>本次请求的 span 句柄；未开观测时为默认值。</summary>
        internal AxiosRequestTrace Trace => _trace;

        /// <summary>请求成功：交给观测实现记录；收尾仍由 <see cref="Dispose"/> 统一执行。</summary>
        /// <param name="config">本次请求的生效配置。</param>
        /// <param name="uri">最终 URL。</param>
        /// <param name="response">已落库的响应。</param>
        internal void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response)
        {
            if (_entered)
            {
                _sink!.RequestSucceeded(config, uri, response, _start, _trace);
            }
        }

        /// <summary>请求失败：错误原样记录，异常本身仍由调用方抛出。</summary>
        /// <param name="config">本次请求的生效配置；进入观测之前就失败时为 <c>null</c>。</param>
        /// <param name="uri">最终 URL；进入观测之前就失败时为 <c>null</c>。</param>
        /// <param name="error">触发失败的异常。</param>
        internal void RequestFailed(AxiosConfig? config, Uri? uri, Exception error)
        {
            if (_entered)
            {
                _sink!.RequestFailed(config, uri, error, _start, _trace);
            }
        }

        /// <summary>退出观测并停止 span；异常不外抛。</summary>
        public void Dispose() => _scope.Dispose();
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

        /// <summary>先退出观测、再收 span；两者的异常都由派发层吞掉。</summary>
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
                _trace.Stop();
            }
        }
    }
}
