using System.Diagnostics;
using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的请求管线：合并 → 拦截 → 规范化 → 编码，以及一次请求的成型（URL / 协议 / 正文 / 请求头）。
public sealed partial class AxiosClient
{
    /// <summary>请求入口：按固定顺序驱动整条管线 —— 合并 → 校验（实例级）→ 拦截 → 规范化 →（短路 | 校验 + 闸门 → 编码 → 发送）→ 收尾；顺序是契约，理由见各阶段方法。</summary>
    private async Task<AxiosResponse> SendAsync(
        AxiosConfig perCallConfig,
        Type? dataType,
        AxiosResponse? target = null)
    {
        // 阶段 0：守卫。放在 try 之外：它抛时名额还没占上，finally 不该归还。
        EnterInFlight(perCallConfig);

        // 总预算的计时起点：调用入口。合并与拦截器耗时都算在调用方感知的时长里。
        var entryTimestamp = Stopwatch.GetTimestamp();

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

            // 总预算换成一次绝对时间戳：重试 / 换端点 / 对冲分支共享同一个截止点，晚起跑的分支拿到更少的剩余。
            var deadlineTimestamp = DeadlineBudget.Start(config, entryTimestamp);

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
                // 正文随模板一起释放；重试换出来的请求只摘不释放（见 Attempt.RetryLoopAsync 的 finally）。
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
}
