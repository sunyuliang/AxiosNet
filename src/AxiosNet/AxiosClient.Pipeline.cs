using System.Diagnostics;
using AxiosNet.Cache;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的请求管线：合并 → 拦截 → 规范化 → 编码，以及一次请求的成型（URL / 协议 / 正文 / 请求头）。
public sealed partial class AxiosClient
{
    /// <summary>请求入口：按固定顺序驱动整条管线 —— 合并 → 校验（实例级）→ 拦截 → 规范化 →（短路 | 校验 + 闸门 → 编码 → 发送）→ 收尾。</summary>
    private async Task<AxiosResponse> SendAsync(
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

        try
        {
            // 阶段 1：合并 —— 得到本次请求的生效配置。
            config = MergeEffectiveConfig(perCallConfig);

            // 阶段 2：校验（实例级选项）—— 构造期已固化进连接池，判在拦截器之前。
            ValidatePerRequestOptions(config);

            // 阶段 3：拦截 —— 请求拦截器按注册逆序执行（与 axios 一致）。
            config = await InterceptRequestAsync(config).ConfigureAwait(false);

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

            // 阶段 5：编码 —— transformRequest → URL / 协议 → 正文 / 请求头。
            ApplyRequestTransforms(config);
            var plan = BuildRequestPlan(config);

            // 阶段 5.5：缓存 —— 命中不发请求（不建正文、不占闸门、不产生 span）；未命中时同键并发只发一次。
            if (config.Cache is { } configuredCache && configuredCache.CanHandle(config))
            {
                cache = configuredCache;
                cacheKey = configuredCache.KeyFor(config, plan.Uri);

                if (await configuredCache.TryGetAsync(cacheKey, config.CancellationToken).ConfigureAwait(false) is { } cachedReply)
                {
                    return await CompleteShortCircuitAsync(cachedReply, config, dataType, target).ConfigureAwait(false);
                }

                if (configuredCache.SingleFlight)
                {
                    flight = configuredCache.Join(cacheKey);

                    if (!flight.IsLeader)
                    {
                        try
                        {
                            var sharedReply = await AwaitFlightAsync(flight, config).ConfigureAwait(false);

                            if (sharedReply is not null)
                            {
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

                // 由管线决定要不要留原始字节：只有真的会尝试落库的请求才留。
                config.CacheCaptureRawBody = true;
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

            // 总预算换成绝对时间戳：各分支共享同一截止点。
            var deadlineTimestamp = TotalTimeoutBudget.Start(config, entryTimestamp);

            // 模板持有请求体（可能是调用方的 FileStream / FormFile），所有路径都必须先释放它。
            try
            {
                // 阶段 6：发送 —— 响应拦截器链 + 尝试管线（重试 / 备用端点 / 熔断 / 对冲 / 闸门 / 终端，见 AxiosAttemptPipeline）。
                var attempt = AxiosAttemptResult.Ensure(
                    await RunResponseInterceptorsAsync(
                        template,
                        AxiosAttemptContext.From(config, plan.Uri, dataType, target, gate, deadlineTimestamp)).ConfigureAwait(false),
                    config);

                var result = attempt.Response;
                var streamOwner = attempt.StreamOwner;

                // 拦截器返回新实例时释放原流式正文。
                if (streamOwner is not null && !ReferenceEquals(result.Data, streamOwner))
                {
                    streamOwner.Dispose();
                }

                if (telemetryEnabled)
                {
                    // 必须在 template.Dispose()（外层 finally）之前：之后正文头不可读。
                    AxiosTelemetry.RequestSucceeded(config, plan.Uri, result, telemetryStart, telemetryActivity);
                }

                // 落库 + 放行跟随者：两者都要在返回之前完成，跟随者读的就是这份结果。
                if (flight is not null && cache is not null && cacheKey is not null)
                {
                    await CompleteCacheFlightAsync(cache, cacheKey, flight, result).ConfigureAwait(false);
                    flight = null;
                }

                return result;
            }
            finally
            {
                // 正文随模板一起释放；重试换出来的请求只摘不释放（见 Attempt.RetryLoopAsync 的 finally）。
                template.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 领导者失败：跟随者原样拿到这个错误，不要挂在原地等。
            if (flight is not null && cache is not null && cacheKey is not null)
            {
                cache.FailFlight(cacheKey, flight, ex);
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
                config.Data = transform(config.Data, config);
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

        /// <summary>请求的 HTTP 版本（默认 2.0）。</summary>
        public Version Version { get; }

        /// <summary>版本协商策略（默认 <see cref="HttpVersionPolicy.RequestVersionOrLower"/>）。</summary>
        public HttpVersionPolicy VersionPolicy { get; }
    }

    /// <summary>
    /// 阶段 5b —— 成型：拼出最终 URL、确定协议协商参数，并做 HTTP/3 的前置校验。
    /// 默认 HTTP/2 + <see cref="HttpVersionPolicy.RequestVersionOrLower"/>；明文 http:// 不改写版本；
    /// 强制 1.1 写 Version11，要 h3 写 Version30。
    /// </summary>
    private static RequestPlan BuildRequestPlan(AxiosConfig config)
    {
        var uri = UrlBuilder.Build(config.BaseAddress, config.Url, config.Params, config.SerializationOrNull?.ParamsSerializer, config: config);

        var connection = config.ConnectionOrNull;
        var httpVersion = connection?.HttpVersion ?? HttpVersion.Version20;
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
                // 序列化失败走统一错误模型，声明式表单报模型名。
                throw AxiosException.BadRequestPayload(
                    config,
                    template,
                    config.Data is DeclaredForm declared
                        ? declared.SourceName ?? declared.GetType().Name
                        : config.Data?.GetType().Name ?? "null",
                    ex);
            }

            // 上传进度 / 限速：包一层流量统计（头部复制）。
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

                // 长度已知时写进 span，未知则不写。
                AxiosTelemetry.SetRequestBodySize(telemetryActivity, content.Headers.ContentLength);
            }

            ApplyHeaders(template, config);
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
