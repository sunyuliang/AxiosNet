using System.Diagnostics;
using System.Runtime.CompilerServices;
using AxiosNet.Cache;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的请求管线：合并 → 拦截 → 规范化 → 编码，以及一次请求的成型（URL / 协议 / 正文 / 请求头）。
public sealed partial class AxiosClient
{
    /// <summary>请求入口：按固定顺序驱动整条管线 —— 合并 → 校验（实例级）→ 拦截 → 规范化 / 范围校验 →（短路响应 | 编码 → 缓存查询 → 尝试管线 → 缓存落库）→ 收尾。</summary>
    /// <remarks>公开入口要返回真正的 <see cref="Task{TResult}"/>（可多次 await / 交给调用方保存），这一步只做适配。</remarks>
    private Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
        => SendAsyncCore(perCallConfig, dataType, target).AsTask();

    /// <summary>管线核心：与 <see cref="SendAsync"/> 同一条流程，状态机用池化 builder。</summary>
    /// <remarks>
    /// <c>PoolingAsyncValueTaskMethodBuilder</c> 复用状态机盒；返回的 <c>ValueTask</c> 由 <c>AsTask()</c>
    /// 一次性转成 Task 后交给调用方，池化源只被消费一次。
    /// </remarks>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性，
    // 只改变 ValueTask 状态机的分配策略，不改变请求语义；net6.0 / net8.0 仍走池化 builder。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<AxiosResponse> SendAsyncCore(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // 阶段 0：守卫（在 try 之外，抛时名额还没占上，不归还）。
        EnterInFlight(perCallConfig);

        // 总预算的计时起点：调用入口（含合并与拦截器耗时）。
        var entryTimestamp = Stopwatch.GetTimestamp();

        // 遥测状态全是局部量；后三项在 URL 拼好后才定。
        AxiosConfig? config = null;
        Uri? telemetryUri = null;
        var telemetryEnabled = false;
        var telemetryStart = 0L;
        Activity? telemetryActivity = null;

        // 缓存状态：命中直接返回；未命中时由领导者落库并放行跟随者。
        AxiosCache? cache = null;
        string? cacheKey = null;
        AxiosCacheFlight? flight = null;
        CacheRequestDirectives cacheDirectives = CacheRequestDirectives.None;
        var cacheMissed = false;

        try
        {
            // 阶段 1：合并 —— 得到本次请求的生效配置。
            config = MergeEffectiveConfig(perCallConfig);

            // 阶段 2：校验（实例级选项）—— 构造期已固化进连接池，判在拦截器之前。
            ValidatePerRequestOptions(config);

            // 阶段 2.5：凭据 —— 槽位在拦截器之前解析成 Authorization 头值；拦截器后只在槽位变化时补解析，
            // 提供方在本次请求内不会被无谓地调用第二次。
            var authBeforeIntercept = config.TransportOrNull?.Auth;
            var resolvedBeforeIntercept = config.ResolvedAuthorization;
            var explicitHeaderBeforeResolve = HasAuthorizationHeader(config);
            await ResolveCredentialAsync(config).ConfigureAwait(false);

            // 提供方可以在解析时自己写 Authorization 头（自定义凭据形态）：那不算「显式头与槽位并存」的冲突。
            var headerFromProvider = HasAuthorizationHeader(config);

            // 阶段 3：拦截 —— 请求拦截器按注册逆序执行（与 axios 一致）。
            config = await InterceptRequestAsync(config).ConfigureAwait(false);

            // 阶段 3.5：拦截器可能换掉配置、改凭据槽位或加上显式 Authorization 头：
            // 冲突校验与解析补到拦截器之后，判的是最终要发出的那份配置。
            // 只在「配置本身已有显式头」或「拦截器新加了头」时校验；提供方解析时自己写的头不算冲突。
            if (explicitHeaderBeforeResolve || (HasAuthorizationHeader(config) && !headerFromProvider))
            {
                ValidateCredentialConflict(config);
            }

            if (ReferenceEquals(authBeforeIntercept, config.TransportOrNull?.Auth))
            {
                // 槽位没变：沿用拦截器前的解析结果（拦截器换出的新配置不会带走内部解析值）。
                if (config.ResolvedAuthorization is null && resolvedBeforeIntercept is not null)
                {
                    config.ResolvedAuthorization = resolvedBeforeIntercept;
                }
            }
            else
            {
                // 槽位被换掉 / 清空：按最终槽位重新解析（清空时同时作废旧头值）。
                await ResolveCredentialAsync(config).ConfigureAwait(false);
            }

            // 阶段 4a ~ 4d 都必须使用拦截器之后的配置。
            ApplyAutoResponseType(config, dataType);

            // 范围校验与是否真的发出请求无关，放在短路之前。
            ValidateRangeConfig(config);

            // 闸门与编码只对真实 HTTP 操作有意义，短路不经过它们。
            if (config.ShortCircuit is { } shortCircuit)
            {
                return await CompleteShortCircuitAsync(shortCircuit, config, dataType, target).ConfigureAwait(false);
            }

            var gate = ResolveConcurrencyGate(config);

            // 总预算换成绝对时间戳：各分支共享同一截止点（跟随者等缓存结果同样受它约束）。
            var deadlineTimestamp = TotalTimeoutBudget.Start(config, entryTimestamp);

            // 阶段 5：编码 —— transformRequest → URL / 协议 → 正文 / 请求头。
            ApplyRequestTransforms(config);
            var plan = BuildRequestPlan(config);

            // 阶段 5.5：缓存 —— 命中不发请求（不建正文、不占闸门、不产生 span）；未命中时同键并发只发一次。
            if (config.Caching is { } configuredCache && configuredCache.CanHandle(config))
            {
                cache = configuredCache;
                cacheKey = configuredCache.KeyFor(config, plan.Uri);

                // 请求侧的 Cache-Control / Pragma：只有非 Configured 模式才解析（Configured 是「强制按配置执行」）。
                cacheDirectives = configuredCache.ReadRequestDirectives(config);

                // 请求要求绕开缓存（no-store / no-cache / max-age=0）：不查、不参与单飞，但仍会尝试落库。
                if (!cacheDirectives.SkipLookup)
                {
                    if (await configuredCache.TryGetAsync(cacheKey, config, deadlineTimestamp).ConfigureAwait(false) is { } cachedReply)
                    {
                        AxiosTelemetry.CacheHit(config);
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
                                    // 跟随者拿到领导者的结果：这次请求同样没发出。
                                    AxiosTelemetry.CacheHit(config);
                                    cacheMissed = false;
                                    return await CompleteShortCircuitAsync(sharedReply, config, dataType, target).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                // 跟随者不落库，也不参与「领导者失败」的广播。
                                flight = null;
                            }
                        }
                    }
                }

                // 查过但没有、且这次真要发出去：计一次未命中（绕开缓存的请求不计，命中率口径才干净）。
                if (cacheMissed)
                {
                    AxiosTelemetry.CacheMiss(config);
                }

                // 由管线决定要不要留原始字节：请求侧已否决落库（no-store / max-age=0）时不必为缓存复制正文。
                config.CacheCaptureRawBody = cacheDirectives.CanStore;

                // 条件请求：新鲜条目没命中时，拿一条带校验器的旧条目，把校验器写成条件头再发。
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

            // 401 重放要在第二次发送前继续持有正文；只有失败确实是 401 时才把正文交接出去。
            AxiosException? attemptFailure = null;

            // 模板持有请求体（可能是调用方的 FileStream / FormFile），所有路径都必须先释放它。
            try
            {
                // 阶段 6：发送 —— 响应拦截器链 + 尝试管线（重试 / 备用端点 / 熔断 / 对冲 / 闸门 / 审计 / 终端，见 AxiosAttemptPipeline）。
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

                // 条件请求的 304：请求确实发出去了（已按未命中计入命中率），正文来自缓存 —— 单独计数，
                // 面板才看得出「省了多少带宽」。
                if (result.CacheRevalidated)
                {
                    AxiosTelemetry.CacheRevalidated(config, telemetryActivity);
                }

                // 落库 + 放行跟随者：两者都要在返回之前完成，跟随者读的就是这份结果。
                // 关掉单飞时 flight 为 null，这里只落库（否则缓存永远没有条目）。
                if (cache is not null && cacheKey is not null)
                {
                    await StoreAsync(cache, cacheKey, flight, config, cacheDirectives, result, deadlineTimestamp).ConfigureAwait(false);
                    flight = null;
                }

                if (telemetryEnabled)
                {
                    // 放在落库之后：span 与耗时直方图都覆盖「调用方拿到结果」的完整等待时间（存储慢也算进去）。
                    // 仍必须在 template.Dispose()（外层 finally）之前：之后正文头不可读。
                    AxiosTelemetry.RequestSucceeded(config, plan.Uri, result, telemetryStart, telemetryActivity);
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
                // 正文随模板一起释放；重试换出来的请求只摘不释放（见 Attempt.RetryLoopAsync 的 finally）。
                // WebAPI 401 重放需要在第二次发送前继续持有正文：只把这次正文交给失败的配置，
                // 由门面在重放 / 放弃后释放；成功路径与其它失败照旧在这里释放。
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
            // 领导者失败时的跟随者处理：
            // - HTTP 状态类失败（Response 非 null）：把状态码 / 响应头 / 原始正文共享给跟随者，
            //   每个跟随者用自己的 ValidateStatus / 响应拦截器 / 错误工厂判定，服务端全程只发一次。
            //   流式响应、没拿到原始字节或正文超过共享上限时无法共享（reply = null），跟随者各自补发。
            // - 传输 / 取消类失败与具体请求口径无关，按约定广播，避免同时压垮同一个下游。
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

            // 失败路径不会落库：内部缓冲只服务单飞共享（上面已复制一份），再挂着只会让整段错误正文
            // 跟着异常一起存活。对外的 RawBody 视图（若开启）是独立引用，不受影响。
            if (ex is AxiosException { Response: { } failedResponse })
            {
                failedResponse.RawBodyBuffer = null;
            }

            // 记录后原样抛出，不改动异常。
            if (telemetryEnabled)
            {
                AxiosTelemetry.RequestFailed(config, telemetryUri, ex, telemetryStart, telemetryActivity);
            }

            throw;
        }
        finally
        {
            // 条件请求的旧条目只属于这一次发送：留在 response.Config 上会拖着整段缓存正文，
            // 重放这份配置还会带上已经过时的 If-None-Match（与条件头不写进配置头同一理由）。
            if (config is not null)
            {
                config.RevalidationEntry = null;
            }

            if (telemetryEnabled)
            {
                AxiosTelemetry.ExitRequest();
            }

            // 必须收 span，否则后续请求的 span 会错挂。
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

            // 拦截器可以返回全新配置（拦截器自己 new 的，或调用方原始输入）：它没经过阶段 1 的合并，
            // 缺少实例默认值（BaseAddress / 超时 / 连接级选项等），必须补一次合并才能发出。
            // 已合并的配置（EffectiveMerged）与它的克隆（InstanceOptionsInherited）不再合并：
            // 第二次合并会以实例默认值为基底，把拦截器刚写入的项（如按请求 BaseAddress / 并发）冲掉。
            // 合并按 *Set 标记取值，拦截器显式写入的项不会被默认值覆盖。
            if (!next.EffectiveMerged && !next.InstanceOptionsInherited)
            {
                config = MergeConfig(next);
            }
        }

        return config;
    }

    /// <summary>
    /// 阶段 4a —— 规范化：泛型 <c>&lt;dynamic&gt;</c> / <c>&lt;object&gt;</c> 与 <c>&lt;Stream&gt;</c> 自动切换响应形态。
    /// 判据是 <see cref="AxiosConfig.ResponseTypeSet"/>；切换本身不置该标记。
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

    /// <summary>
    /// 阶段 5b —— 成型：拼出最终 URL、确定协议协商参数，并做 HTTP/3 的前置校验。
    /// 默认 HTTP/2 + <see cref="HttpVersionPolicy.RequestVersionOrLower"/>（netstandard2.0 兼容资产默认 1.1）；
    /// 明文 http:// 不改写版本；强制 1.1 写 Version11，要 h3 写 Version30。
    /// </summary>
    private static RequestPlan BuildRequestPlan(AxiosConfig config)
    {
        var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.QueryEncoder, config: config);

        var connection = config.TransportOrNull;
#if NETSTANDARD2_0
        // 兼容资产的 HttpClientHandler 只能发 HTTP/1.1：默认版本必须是 1.1，
        // 不能沿用 2.0（Framework 的 System.Net.Http 里连 HttpVersion.Version20 字段都不存在）。
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version11;
#else
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version20;
#endif
        var versionPolicy = connection?.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower;

        // HTTP/3 前置校验：只在真的要 h3 时执行。
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
                // 序列化失败走统一错误模型，带来源名称的表单容器报该名称。
                throw AxiosException.BadRequestPayload(
                    config,
                    template,
                    config.Body is FormData declared
                        ? declared.SourceName ?? declared.GetType().Name
                        : config.Body?.GetType().Name ?? "null",
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

                // 长度已知时写进 span，未知则不写。
                AxiosTelemetry.SetRequestBodySize(telemetryActivity, content.Headers.ContentLength);
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
}
