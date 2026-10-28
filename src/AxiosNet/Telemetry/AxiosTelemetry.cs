using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Retry;

namespace AxiosNet.Telemetry;

/// <summary>
/// 库的观测点：链路（<see cref="ActivitySource"/>）、指标（<see cref="Meter"/>）与事件（<see cref="DiagnosticListener"/>），
/// 三者共用「AxiosNet」这个名字；量的是请求成型到响应读完（含拦截器与重试），观测异常一律不影响请求结果。
/// </summary>
public static class AxiosTelemetry
{
    /// <summary>链路源名：OpenTelemetry 用 <c>AddSource("AxiosNet")</c> 订阅。</summary>
    public const string ActivitySourceName = "AxiosNet";

    /// <summary>指标源名：OpenTelemetry 用 <c>AddMeter("AxiosNet")</c> 订阅。</summary>
    public const string MeterName = "AxiosNet";

    /// <summary>诊断监听器名：<c>DiagnosticListener.AllListeners</c> 里按这个名字过滤。</summary>
    public const string DiagnosticListenerName = "AxiosNet.DiagnosticListener";

    /// <summary>事件名：请求即将发出（载荷 <see cref="AxiosRequestStartEvent"/>）。</summary>
    public const string RequestStartEventName = "AxiosNet.RequestStart";

    /// <summary>事件名：请求成功结束（载荷 <see cref="AxiosRequestStopEvent"/>）。</summary>
    public const string RequestStopEventName = "AxiosNet.RequestStop";

    /// <summary>事件名：请求失败结束（载荷 <see cref="AxiosRequestFailedEvent"/>）。</summary>
    public const string RequestFailedEventName = "AxiosNet.RequestFailed";

    /// <summary>事件名：一次失败尝试后决定重试（载荷 <see cref="AxiosRetryContext"/>）。</summary>
    public const string RetryEventName = "AxiosNet.Retry";

    /// <summary>
    /// 事件名：一次尝试成功结束（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// 只有链上装了 <see cref="Attempt.Audit"/> 时才产生。
    /// </summary>
    public const string AttemptStopEventName = "AxiosNet.AttemptStop";

    /// <summary>
    /// 事件名：一次尝试失败结束（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// 只有链上装了 <see cref="Attempt.Audit"/> 时才产生。
    /// </summary>
    public const string AttemptFailedEventName = "AxiosNet.AttemptFailed";

    /// <summary>指标名：逻辑请求耗时直方图（单位秒，含重试与等待）。</summary>
    public const string RequestDurationInstrumentName = "axiosnet.client.request.duration";

    /// <summary>指标名：当前在途请求数（ObservableGauge，收集时读取）。</summary>
    public const string ActiveRequestsInstrumentName = "axiosnet.client.active_requests";

    /// <summary>指标名：重试次数（Counter，每次「决定重试」计 1），带 <c>axiosnet.retry.reason</c> 标签。</summary>
    public const string RetriesInstrumentName = "axiosnet.client.request.retries";

    /// <summary>指标名：正在排队等并发槽位的请求数（ObservableGauge）。</summary>
    public const string GateQueueDepthInstrumentName = "axiosnet.client.gate.queue_depth";

    /// <summary>指标名：熔断器各状态的维度数（ObservableGauge），带 <c>axiosnet.circuit.state</c> 标签。</summary>
    public const string CircuitStateInstrumentName = "axiosnet.client.circuit.state";

    /// <summary>指标名：租约次数（Counter），带 <c>axiosnet.pool.endpoint.reused</c> 标签（<c>true</c> / <c>false</c>）；<b>不带 host</b>，见 <see cref="RecordLease"/>。</summary>
    public const string PoolLeasesInstrumentName = "axiosnet.client.pool.leases";

    /// <summary>指标名：新建 origin 节点数（Counter），不带 host（低基数）。</summary>
    public const string PoolEndpointsCreatedInstrumentName = "axiosnet.client.pool.endpoints.created";

    /// <summary>指标名：驱逐 origin 节点数（Counter），带 <c>axiosnet.pool.eviction.reason</c> 标签，不带 host。</summary>
    public const string PoolEndpointsEvictedInstrumentName = "axiosnet.client.pool.endpoints.evicted";

    /// <summary>指标名：当前活跃租约数（ObservableGauge），正文下载期间仍然计入。</summary>
    public const string ActiveLeasesInstrumentName = "axiosnet.client.pool.active_leases";

    /// <summary>指标名：当前存活（未 <c>Dispose</c>）的连接池数（ObservableGauge）。</summary>
    public const string ActivePoolsInstrumentName = "axiosnet.client.pool.active_pools";

    /// <summary>指标名：命名共享池表里的条目数（ObservableGauge），即 <see cref="Pooling.HttpConnectionPool"/> 的 <c>SharedCount</c>。</summary>
    public const string SharedPoolsInstrumentName = "axiosnet.client.pool.shared_pools";

    // 驱逐原因：指标标签的取值，属对外契约（后端按它分面板）。
    internal const string EvictionReasonIdle = "idle";
    internal const string EvictionReasonCapacity = "capacity";
    internal const string EvictionReasonManual = "manual";
    internal const string EvictionReasonClear = "clear";
    internal const string EvictionReasonDispose = "dispose";

    // 标签名一律取 OpenTelemetry HTTP 语义约定的标准名（v1.27）：后端 / Collector 无需额外映射。
    private const string TagMethod = "http.request.method";
    private const string TagScheme = "url.scheme";
    private const string TagPath = "url.path";
    private const string TagFullUrl = "url.full";
    private const string TagServerAddress = "server.address";
    private const string TagServerPort = "server.port";
    private const string TagStatusCode = "http.response.status_code";
    private const string TagProtocolVersion = "network.protocol.version";
    private const string TagErrorType = "error.type";
    private const string TagResendCount = "http.request.resend_count";
    private const string TagRequestBodySize = "http.request.body.size";
    private const string TagResponseBodySize = "http.response.body.size";

    // 库自有标签（语义约定没有对应项）：一律 axiosnet. 前缀，与已有的 axiosnet.retry.delay_ms 一致。
    private const string TagErrorCode = "axiosnet.error.code";
    private const string TagEndpointReused = "axiosnet.pool.endpoint.reused";
    private const string TagEvictionReason = "axiosnet.pool.eviction.reason";
    private const string TagRetryReason = "axiosnet.retry.reason";
    private const string TagCircuitState = "axiosnet.circuit.state";

    // 重试原因：低基数取值。有状态码就统一报 "status"（具体状态码在 error.type 里），否则用库错误码。
    internal const string RetryReasonStatus = "status";
    internal const string RetryReasonException = "exception";

    // 熔断状态：指标标签的取值，属对外契约（后端按它分面板）。
    internal const string CircuitStateClosed = "closed";
    internal const string CircuitStateOpen = "open";
    internal const string CircuitStateHalfOpen = "half_open";

    internal static readonly ActivitySource Source;

    internal static readonly DiagnosticListener Listener;

    private static readonly Histogram<double> RequestDuration;

    private static readonly Counter<long> Retries;

    private static readonly Counter<long> PoolLeases;

    private static readonly Counter<long> PoolEndpointsCreated;

    private static readonly Counter<long> PoolEndpointsEvicted;

    /// <summary>在途请求数（全进程）。</summary>
    private static long _activeRequests;

    /// <summary>活跃租约数（全进程，跨所有池）：正文下载期间仍然计入。</summary>
    private static long _activeLeases;

    /// <summary>存活池数（全进程）：建池 +1、<c>Dispose</c> -1，用来发现「池被建了太多」。</summary>
    private static long _livePools;

    static AxiosTelemetry()
    {
        // 版本跟着程序集走：后端据此区分同一个源的不同版本。
        var version = typeof(AxiosTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        Source = new ActivitySource(ActivitySourceName, version);
        Listener = new DiagnosticListener(DiagnosticListenerName);

        var meter = new Meter(MeterName, version);
        RequestDuration = meter.CreateHistogram<double>(RequestDurationInstrumentName, unit: "s", description: "AxiosNet 逻辑请求耗时（含重试与退避等待），单位秒。");

        // net6.0 没有 UpDownCounter，改用 ObservableGauge：进 / 出各一次 Interlocked，收集时读取。
        meter.CreateObservableGauge(ActiveRequestsInstrumentName, () => Volatile.Read(ref _activeRequests), unit: "{request}", description: "已成型（URL 已拼好）并进入发送阶段、尚未结束的请求数。");

        Retries = meter.CreateCounter<long>(RetriesInstrumentName, unit: "{retry}", description: "按策略决定重试的次数（不含首次请求）；axiosnet.retry.reason 区分状态码 / 网络 / 超时等原因。");

        // 排队深度：容量规划最直接的信号（「现在排了多长」而不是「排队超时了多少次」）。
        // 与在途数同一手法：静态计数 + 收集时读取；闸门实例可以有很多个，这里看的是进程总量。
        meter.CreateObservableGauge(GateQueueDepthInstrumentName, () => ConcurrencyGate.QueueDepth, unit: "{request}", description: "当前正在等并发槽位的请求数（进程级，跨所有客户端）。");

        // 熔断状态：按 state 报「多少个维度处于该状态」，不带 host —— 与池指标同一套低基数规则。
        meter.CreateObservableGauge(CircuitStateInstrumentName, ObserveCircuitStates, unit: "{circuit}", description: "熔断器跟踪的维度数，按 axiosnet.circuit.state（closed / open / half_open）分组；每个维度 = 一个熔断器实例上的一个 key（默认 host:port）。");

        PoolLeases = meter.CreateCounter<long>(PoolLeasesInstrumentName, unit: "{lease}", description: "成功租用 origin 节点的次数；axiosnet.pool.endpoint.reused=false 表示本次新建了节点。");

        PoolEndpointsCreated = meter.CreateCounter<long>(PoolEndpointsCreatedInstrumentName, unit: "{endpoint}", description: "新建的 origin 节点数（Handler + HttpClient），即节点未命中的次数。");

        PoolEndpointsEvicted = meter.CreateCounter<long>(PoolEndpointsEvictedInstrumentName, unit: "{endpoint}", description: "被驱逐并释放的 origin 节点数，按 axiosnet.pool.eviction.reason 区分原因。");

        // 与在途请求数同一手法：计的是持有租约的请求，含正文还没读完的流式响应。
        meter.CreateObservableGauge(ActiveLeasesInstrumentName, () => Volatile.Read(ref _activeLeases), unit: "{lease}", description: "当前持有 origin 节点租约的请求数（含正文下载中的流式响应）。");

        // 池的个数而不是池里的连接数：这条水位上涨 = 有地方在反复建池（典型是「按请求 new 客户端」）。
        meter.CreateObservableGauge(ActivePoolsInstrumentName, () => Volatile.Read(ref _livePools), unit: "{pool}", description: "当前存活（未 Dispose）的连接池数；持续增长说明有代码在反复建池。");

        // 条目数是 int（字典 Count），统一抬成 long：与其余水位的观测类型一致。
        meter.CreateObservableGauge(SharedPoolsInstrumentName, () => (long)Pooling.HttpConnectionPool.SharedCount, unit: "{pool}", description: "命名共享池表（HttpConnectionPool.GetOrCreate）里的条目数。");
    }

    /// <summary>单调时钟时间戳（配合 <see cref="ElapsedSeconds"/> 算耗时，不受系统时间调整影响）。</summary>
    internal static long Timestamp => Stopwatch.GetTimestamp();

    /// <summary>本次请求是否产出遥测（默认开启；<paramref name="config"/> 为 null 视为「没表态」）。</summary>
    internal static bool IsEnabled(AxiosConfig? config) => config?.TelemetryOrNull?.Enabled ?? true;

    /// <summary>耗时秒数：从 <see cref="Timestamp"/> 到现在的间隔。</summary>
    internal static double ElapsedSeconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;

    /// <summary>请求进入发送管线：在途 +1（与 <see cref="ExitRequest"/> 必须成对）。</summary>
    internal static void EnterRequest() => Interlocked.Increment(ref _activeRequests);

    /// <summary>请求离开发送管线：在途 -1。</summary>
    internal static void ExitRequest() => Interlocked.Decrement(ref _activeRequests);

    /// <summary>租约生效：活跃租约 +1（与 <see cref="ExitLease"/> 必须成对）。</summary>
    internal static void EnterLease() => Interlocked.Increment(ref _activeLeases);

    /// <summary>租约归还：活跃租约 -1。</summary>
    internal static void ExitLease() => Interlocked.Decrement(ref _activeLeases);

    /// <summary>建成一条池：存活池数 +1（与 <see cref="ExitPool"/> 必须成对，池的 <c>Dispose</c> 幂等）。</summary>
    internal static void EnterPool() => Interlocked.Increment(ref _livePools);

    /// <summary>池被释放：存活池数 -1。</summary>
    internal static void ExitPool() => Interlocked.Decrement(ref _livePools);

    /// <summary>
    /// 一次成功的租约：<c>pool.leases</c> +1（带 <c>reused</c> 标签），节点是新建时再记一条
    /// <c>pool.endpoints.created</c>。池域指标不受 <see cref="AxiosConfig.TelemetrySettings.Enabled"/> 约束。
    /// </summary>
    /// <remarks>
    /// <b>刻意不带 host / port</b>：网关、爬虫、多租户 SaaS 会访问成千上万个 origin，一个 host 一条时间序列
    /// 会把指标后端打爆（OpenTelemetry 点名的 cardinality 反模式）。要定位到具体 origin 就看 span ——
    /// 它本来就有 <c>server.address</c> / <c>server.port</c> 与 <c>axiosnet.pool.endpoint.reused</c>。
    /// </remarks>
    internal static void RecordLease(bool reused)
    {
        if (!PoolLeases.Enabled)
        {
            return;
        }

        try
        {
            PoolLeases.Add(1, LeaseTags(reused));
        }
        catch
        {
            // 测量回调抛异常不能影响租用（见类注释）
        }
    }

    /// <summary>新建了一个 origin 节点：<c>pool.endpoints.created</c> +1（不带 host，理由见 <see cref="RecordLease"/>）。</summary>
    internal static void RecordEndpointCreated()
    {
        if (!PoolEndpointsCreated.Enabled)
        {
            return;
        }

        try
        {
            // 无标签计数：单参数重载不构造 TagList，也不分配标签数组。
            PoolEndpointsCreated.Add(1);
        }
        catch
        {
        }
    }

    /// <summary>驱逐了一个 origin 节点：<c>pool.endpoints.evicted</c> +1，带原因（不带 host）。</summary>
    internal static void RecordEndpointEvicted(string reason)
    {
        if (!PoolEndpointsEvicted.Enabled)
        {
            return;
        }

        try
        {
            PoolEndpointsEvicted.Add(1, new KeyValuePair<string, object?>(TagEvictionReason, reason));
        }
        catch
        {
        }
    }

    /// <summary>把「这次租约是否复用了已有节点」写到当前 span 上（只写本库的 span）。</summary>
    internal static void TagPoolReuse(bool reused)
    {
        var activity = Activity.Current;
        if (activity is null || !ReferenceEquals(activity.Source, Source))
        {
            return;
        }

        try
        {
            // 重试时会被再次调用：一旦写过就不再覆盖，避免冲掉「本次付了建节点代价」这条信息。
            if (reused && activity.GetTagItem(TagEndpointReused) is not null)
            {
                return;
            }

            // 用 "true"/"false" 字面量而不是 bool：驻留字符串，不产生装箱分配。
            activity.SetTag(TagEndpointReused, reused ? "true" : "false");
        }
        catch
        {
        }
    }

    private static TagList LeaseTags(bool reused) => new()
    {
        { TagEndpointReused, reused ? "true" : "false" }
    };

    /// <summary>
    /// 熔断状态的分组计数：<c>closed</c> 由「已跟踪维度 - 打开 - 半开」推出，不必单独维护一个计数器
    /// （三条计数各自在状态迁移时改一次，合计永远等于跟踪表大小）。
    /// </summary>
    private static IEnumerable<Measurement<long>> ObserveCircuitStates()
    {
        var open = CircuitBreaker.OpenCount;
        var halfOpen = CircuitBreaker.HalfOpenCount;
        var closed = CircuitBreaker.TrackedCount - open - halfOpen;

        if (closed < 0)
        {
            // 并发采样下可能读到瞬时不一致：宁可报 0，也不给后端一个负数。
            closed = 0;
        }

        yield return new Measurement<long>(closed, new KeyValuePair<string, object?>(TagCircuitState, CircuitStateClosed));
        yield return new Measurement<long>(open, new KeyValuePair<string, object?>(TagCircuitState, CircuitStateOpen));
        yield return new Measurement<long>(halfOpen, new KeyValuePair<string, object?>(TagCircuitState, CircuitStateHalfOpen));
    }

    /// <summary>请求已成型（拦截器与 transformRequest 跑完）：开 span 并发出 <see cref="RequestStartEventName"/>；无监听者时返回 <c>null</c>。</summary>
    internal static Activity? StartRequest(AxiosConfig config, Uri uri)
    {
        // 本次请求被显式关掉：不建 span、不发事件（指标与在途计数由调用方按同一判定跳过）
        if (!IsEnabled(config))
        {
            return null;
        }

        Activity? activity = null;

        try
        {
            if (Source.HasListeners())
            {
                // span 名按语义约定取「方法」，URL 放在标签里：名称带 URL 会让后端按 URL 分组成千上万个「操作」。
                activity = Source.StartActivity(config.Method.Method, ActivityKind.Client);

                if (activity is not null)
                {
                    SetRequestTags(activity, config, uri);
                }
            }

            if (Listener.IsEnabled(RequestStartEventName))
            {
                Emit(RequestStartEventName, new AxiosRequestStartEvent(config.Method, uri, config));
            }
        }
        catch
        {
            // 开 span 失败就当没有遥测，请求照发
        }

        return activity;
    }

    /// <summary>收掉 span（等价于 <see cref="Activity.Stop"/>），吞掉监听者抛出的异常。</summary>
    internal static void StopRequest(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            activity.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>请求体字节数（长度未知的流式正文不写这个标签，不猜）。</summary>
    internal static void SetRequestBodySize(Activity? activity, long? size)
    {
        if (activity is null || size is not long value)
        {
            return;
        }

        try
        {
            activity.SetTag(TagRequestBodySize, value);
        }
        catch
        {
        }
    }

    /// <summary>请求成功结束：补状态码 / 协议版本 / 响应字节数，记录耗时指标，发出 <see cref="RequestStopEventName"/>。</summary>
    internal static void RequestSucceeded(
        AxiosConfig config,
        Uri uri,
        AxiosResponse response,
        long startTimestamp,
        Activity? activity)
    {
        // 跑在成功路径上：任何意外都不能变成「请求失败」，连前置计算也一起包进 try。
        try
        {
            var elapsed = ElapsedSeconds(startTimestamp);

            // 4xx / 5xx 即使被 validateStatus 接受也算 HTTP 层错误：span 标 Error、error.type 取状态码。
            var errorType = response.Status >= 400 ? response.Status.ToString(CultureInfo.InvariantCulture) : null;

            if (activity is not null)
            {
                activity.SetTag(TagStatusCode, response.Status);

                if (response.ProtocolVersion is { } protocolVersion)
                {
                    // 协商结果是「线上真正用了什么」，默认 2.0 + 静默降级时这是唯一能确认协议的地方
                    activity.SetTag(TagProtocolVersion, ProtocolVersionTag(protocolVersion));
                }

                if (response.BodyBytes is long bodyBytes)
                {
                    activity.SetTag(TagResponseBodySize, bodyBytes);
                }

                if (errorType is not null)
                {
                    activity.SetTag(TagErrorType, errorType);
                    activity.SetStatus(ActivityStatusCode.Error, $"HTTP {response.Status}");
                }
            }

            RecordDuration(config, uri, response.Status, errorType, elapsed);

            if (Listener.IsEnabled(RequestStopEventName))
            {
                Emit(RequestStopEventName, new AxiosRequestStopEvent(config.Method, uri, response, TimeSpan.FromSeconds(elapsed)));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 请求失败结束：补状态码 / 错误码与异常事件，记录耗时指标，发出 <see cref="RequestFailedEventName"/>。
    /// <paramref name="uri"/> 为 <c>null</c>（请求未成型）时不记指标也不发事件。
    /// </summary>
    internal static void RequestFailed(
        AxiosConfig? config,
        Uri? uri,
        Exception exception,
        long startTimestamp,
        Activity? activity)
    {
        if (config is null || uri is null)
        {
            return;
        }

        // 跑在调用方的 catch 块里：自己抛异常会顶掉原始异常，所以连前置计算也一起包进 try。
        try
        {
            var elapsed = ElapsedSeconds(startTimestamp);
            var axiosException = exception as AxiosException;
            var statusCode = axiosException?.Status;
            var errorType = ErrorTypeOf(exception, statusCode);

            if (activity is not null)
            {
                if (statusCode is int status)
                {
                    activity.SetTag(TagStatusCode, status);
                }

                activity.SetTag(TagErrorType, errorType);

                // 有状态码时 error.type 取状态码，库错误码另写 axiosnet.error.code。
                if (axiosException?.Code is string code)
                {
                    activity.SetTag(TagErrorCode, code);
                }

                activity.SetStatus(ActivityStatusCode.Error, exception.Message);

                // net6.0 没有 Activity.AddException，按语义约定手工记一条 exception 事件。
                activity.AddEvent(new ActivityEvent(
                    "exception",
                    tags: new ActivityTagsCollection
                    {
                        { "exception.type", exception.GetType().FullName },
                        { "exception.message", exception.Message },
                        { "exception.stacktrace", exception.StackTrace }
                    }));
            }

            RecordDuration(config, uri, statusCode, errorType, elapsed);

            if (Listener.IsEnabled(RequestFailedEventName))
            {
                Emit(RequestFailedEventName, new AxiosRequestFailedEvent(config.Method, uri, exception, axiosException?.Code, statusCode, TimeSpan.FromSeconds(elapsed)));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 一次尝试失败后决定重试：重试计数 +1，在 span 上累加 <c>http.request.resend_count</c> 并记一条
    /// <c>retry</c> 事件，同时发出 <see cref="RetryEventName"/>；只写本库的 span，且自己判开关。
    /// </summary>
    internal static void RequestRetried(in AxiosRetryContext context, Uri uri)
    {
        try
        {
            // 重试观测不在主路径的开关分支内，必须自己判一次。
            if (!IsEnabled(context.Config))
            {
                return;
            }

            // 三路都没人订阅时连 error.type 都不必算（它可能是一次 ToString 或异常类型名取字符串）
            var activity = Activity.Current;
            var hasSpan = activity is not null && ReferenceEquals(activity.Source, Source);
            var hasEvent = Listener.IsEnabled(RetryEventName);
            var hasMetric = Retries.Enabled;

            if (!hasSpan && !hasEvent && !hasMetric)
            {
                return;
            }

            var method = context.Method?.Method ?? "GET";

            // 与耗时直方图同一口径：有状态码就用状态码（语义约定），没有才用库错误码
            var errorType = ErrorTypeOf(context.Exception, context.StatusCode);

            // 重试原因：状态码归一到 "status"（具体码在 error.type），其余用库错误码 ——
            // 这样「该调策略（429）还是该修下游（5xx）」在指标上直接分得开，基数仍然有界。
            var retryReason = RetryReasonOf(context.Exception, context.StatusCode);

            if (hasMetric)
            {
                var tags = new TagList
                {
                    { TagMethod, method },
                    { TagServerAddress, HostOf(uri) },
                    { TagServerPort, uri.Port },
                    { TagErrorType, errorType },
                    { TagRetryReason, retryReason }
                };
                Retries.Add(1, tags);
            }

            if (hasSpan)
            {
                activity!.SetTag(TagResendCount, context.Attempt);
                activity.AddEvent(new ActivityEvent(
                    "retry",
                    tags: new ActivityTagsCollection
                    {
                        { "http.request.resend_count", context.Attempt },
                        { "axiosnet.retry.delay_ms", context.Delay.TotalMilliseconds },
                        { "axiosnet.retry.reason", retryReason },
                        { TagStatusCode, context.StatusCode },
                        { TagErrorType, errorType }
                    }));
            }

            if (hasEvent)
            {
                // 结构体装箱只在「确实有人订阅」时发生
                Emit(RetryEventName, context);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 尝试审计是否值得运行：本次请求没关遥测，且至少有一个尝试事件订阅者。
    /// 返回 <c>false</c> 时 <see cref="Attempt.Audit"/> 直接走下一环，不建状态机、不取时间戳。
    /// </summary>
    internal static bool IsAttemptAuditEnabled(AxiosConfig? config)
        => IsEnabled(config)
           && (Listener.IsEnabled(AttemptStopEventName) || Listener.IsEnabled(AttemptFailedEventName));

    /// <summary>一次尝试成功：发出 <see cref="AttemptStopEventName"/>（带状态码与本次尝试耗时）。</summary>
    internal static void AttemptSucceeded(in AxiosAttemptContext context, int? status, long startTimestamp)
    {
        if (!Listener.IsEnabled(AttemptStopEventName))
        {
            return;
        }

        try
        {
            Emit(AttemptStopEventName, new AxiosAttemptEvent(
                context.AttemptNumber,
                context.EndpointIndex,
                context.Config.Method,
                context.Uri,
                TimeSpan.FromSeconds(ElapsedSeconds(startTimestamp)),
                status,
                errorCode: null,
                exception: null));
        }
        catch
        {
        }
    }

    /// <summary>一次尝试失败：发出 <see cref="AttemptFailedEventName"/>（带错误码 / 状态码与本次尝试耗时）。</summary>
    internal static void AttemptFailed(in AxiosAttemptContext context, Exception exception, long startTimestamp)
    {
        if (!Listener.IsEnabled(AttemptFailedEventName))
        {
            return;
        }

        try
        {
            var axiosException = exception as AxiosException;
            Emit(AttemptFailedEventName, new AxiosAttemptEvent(
                context.AttemptNumber,
                context.EndpointIndex,
                context.Config.Method,
                context.Uri,
                TimeSpan.FromSeconds(ElapsedSeconds(startTimestamp)),
                axiosException?.Status,
                axiosException?.Code,
                exception));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 发一条诊断事件；调用前必须先用 <see cref="DiagnosticListener.IsEnabled(string)"/> 判定，因为载荷对象有分配。
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "载荷是本库的公开事件类型（AxiosRequestStartEvent / AxiosRequestStopEvent / "
            + "AxiosRequestFailedEvent / AxiosAttemptEvent / AxiosRetryContext），订阅方直接按类型读属性、不做反射；"
            + "DiagnosticSource.Write 要求「载荷类型静态可发现」是针对反射式订阅方的约束，此处不适用。")]
    private static void Emit(string name, object payload)
    {
        try
        {
            Listener.Write(name, payload);
        }
        catch
        {
            // 订阅方回调抛异常不能影响请求（见类注释）
        }
    }

    /// <summary>span 的静态标签：方法 / 协议 / 主机 / 端口 / 路径（+ 完整 URL）。</summary>
    private static void SetRequestTags(Activity activity, AxiosConfig config, Uri uri)
    {
        activity.SetTag(TagMethod, config.Method.Method);
        activity.SetTag(TagScheme, uri.Scheme);
        activity.SetTag(TagServerAddress, HostOf(uri));
        activity.SetTag(TagServerPort, uri.Port);
        activity.SetTag(TagPath, uri.AbsolutePath);

        if (config.TelemetryOrNull?.CaptureUrl ?? true)
        {
            activity.SetTag(TagFullUrl, UrlForTelemetry(uri, config.TelemetryOrNull?.CaptureQuery ?? false));
        }
    }

    /// <summary>
    /// <c>url.full</c> 的取值：完整 URL，去掉 userinfo；查询串默认不写（<paramref name="includeQuery"/> 为 <c>false</c>）。
    /// </summary>
    private static string UrlForTelemetry(Uri uri, bool includeQuery)
    {
        if (uri.UserInfo.Length == 0 && includeQuery)
        {
            return uri.AbsoluteUri;
        }

        var components = includeQuery ? UriComponents.HttpRequestUrl : UriComponents.HttpRequestUrl & ~UriComponents.Query;

        return uri.GetComponents(components, UriFormat.UriEscaped);
    }

    /// <summary>耗时直方图：标签为方法 / 主机 / 端口 + 状态码 + 错误类型（全是低基数维度）。</summary>
    private static void RecordDuration(
        AxiosConfig config,
        Uri uri,
        int? statusCode,
        string? errorType,
        double seconds)
    {
        if (!RequestDuration.Enabled)
        {
            return;
        }

        var tags = new TagList
        {
            { TagMethod, config.Method.Method },
            { TagServerAddress, HostOf(uri) },
            { TagServerPort, uri.Port }
        };

        if (statusCode is int status)
        {
            tags.Add(TagStatusCode, status);
        }

        if (errorType is not null)
        {
            tags.Add(TagErrorType, errorType);
        }

        RequestDuration.Record(seconds, tags);
    }

    /// <summary><c>error.type</c> 的取值：有 HTTP 状态码就用状态码，否则用库错误码，最后退到异常类型名。</summary>
    private static string ErrorTypeOf(Exception exception, int? statusCode)
    {
        if (statusCode is int status)
        {
            return status.ToString(CultureInfo.InvariantCulture);
        }

        return (exception as AxiosException)?.Code ?? exception.GetType().Name;
    }

    /// <summary>
    /// <c>axiosnet.retry.reason</c> 的取值：有状态码统一报 <c>status</c>（具体码在 <c>error.type</c> 里），
    /// 否则用库错误码（<c>ECONNABORTED</c> / <c>ERR_NETWORK</c> …），最后退到 <c>exception</c>。
    /// 取值集合有界，后端可以安全地按它分面板。
    /// </summary>
    internal static string RetryReasonOf(Exception exception, int? statusCode)
    {
        if (statusCode is not null)
        {
            return RetryReasonStatus;
        }

        return (exception as AxiosException)?.Code ?? RetryReasonException;
    }

    /// <summary><c>network.protocol.version</c> 的取值：语义约定只认 <c>1.0</c> / <c>1.1</c> / <c>2</c> / <c>3</c>。</summary>
    private static string ProtocolVersionTag(Version version) => version.Major switch
    {
        3 => "3",
        2 => "2",
        _ => version.ToString()
    };

    /// <summary>主机名取 IDNA 形式（与连接池的 origin key 同一口径）；IPv6 的 <c>IdnHost</c> 不带方括号，回退 <see cref="Uri.Host"/>。</summary>
    private static string HostOf(Uri uri)
        => uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
}
