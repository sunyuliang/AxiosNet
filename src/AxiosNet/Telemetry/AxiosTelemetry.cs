using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using AxiosNet.Retry;

namespace AxiosNet.Telemetry;

/// <summary>
/// 库的观测点：链路（<see cref="ActivitySource"/>）、指标（<see cref="Meter"/>）与事件
/// （<see cref="DiagnosticListener"/>），三者共用「AxiosNet」这个名字，接 OpenTelemetry 时一行即可。
/// <para>
/// <b>与 HttpClient 的关系</b>：本库最终通过 <c>HttpClient.SendAsync</c> 发送，所以底层那次真实 I/O
/// 的 span 由 BCL 自己产出（.NET 8+ 是 <c>System.Net.Http</c> 源的 <c>HttpRequestOut</c>；.NET 6 只有
/// <c>HttpHandlerDiagnosticListener</c> 事件）。本类补的是**库这一层**：拦截器 / transform 之后、
/// 重试与协议协商之上的一次逻辑请求 —— 包含重试次数、库错误码、实际协商到的 HTTP 版本、请求与响应字节数，
/// 这些 BCL 的 span 里都没有。打开两个源时，BCL 的 span 会自然成为本库 span 的子 span。
/// </para>
/// <para>
/// <b>量的是哪一段</b>：从「请求成型」（请求拦截器与 <c>transformRequest</c> 跑完、URL 已拼好）到「响应读完 +
/// 响应拦截器跑完」。请求侧的准备阶段不计入 —— span 需要 URL 才能开，而指标与 span 必须量同一段，否则同一次
/// 请求在 trace 和面板上会给出两个耗时。开关也在同一处判定，看的是**最终**生效配置，所以请求拦截器改
/// <see cref="AxiosConfig.TelemetrySettings.Enabled"/> 同样生效。
/// </para>
/// <para>
/// <b>零成本约定</b>：三路各有各的闸口，且都判在**组装载荷之前** ——
/// <see cref="ActivitySource.HasListeners"/>、<see cref="DiagnosticListener.IsEnabled(string)"/>、
/// <see cref="Instrument.Enabled"/>。没人订阅时连一个字符串、一个标签数组都不会产生，
/// 只剩每请求两次 <c>Interlocked</c> 与一次 <c>Stopwatch.GetTimestamp()</c>（在途数指标要用）。
/// 完全不想要这点开销就 <c>config.Telemetry.Enabled = false</c>。
/// </para>
/// <para>
/// <b>绝不影响请求结果</b>：所有观测调用都包在 try/catch 里 —— 监听者自己抛异常（自定义
/// <c>ActivityListener</c> / <c>MeterListener</c> 回调）不能让一次正常的 HTTP 请求失败。
/// </para>
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
    /// 指标名：逻辑请求耗时直方图（单位秒，含重试与等待）。
    /// <para>
    /// 接 OpenTelemetry 时要为它配一个 <c>View</c> 指定分桶边界：OTel .NET 的默认边界
    /// （<c>0, 5, 10, 25 … 10000</c>）是按毫秒量纲设计的，而本指标按语义约定用秒，
    /// 不配的话正常请求会全落进 <c>(0, 5]</c> 一个桶，5 ms 与 4 s 分不开。
    /// </para>
    /// </summary>
    public const string RequestDurationInstrumentName = "axiosnet.client.request.duration";

    /// <summary>指标名：当前在途请求数（ObservableGauge，收集时读取）。</summary>
    public const string ActiveRequestsInstrumentName = "axiosnet.client.active_requests";

    /// <summary>指标名：重试次数（Counter，每次「决定重试」计 1）。</summary>
    public const string RetriesInstrumentName = "axiosnet.client.request.retries";

    /// <summary>
    /// 指标名：租约次数（Counter，每次成功租用 origin 节点计 1）。
    /// <para>
    /// 带 <c>axiosnet.pool.endpoint.reused</c> 标签（<c>true</c> / <c>false</c>）：<b>比率请由后端用两个序列算</b>
    /// （<c>1 - reused=false / 全部</c>），不要指望这里给出一个「复用率」Gauge —— 采样点与计数窗口对不齐时，
    /// Gauge 算出来的比率是不可信的。
    /// </para>
    /// <para>
    /// <b>口径边界</b>：这里量的是「池内 origin 节点（Handler + HttpClient）是否命中」，
    /// <b>不是</b> TCP 连接复用 —— 后者发生在 <see cref="System.Net.Http.SocketsHttpHandler"/> 内部，
    /// .NET 6/7 上根本不可观测（.NET 8+ 才有 <c>http.client.connection.duration</c>）。
    /// 一个 origin 每次都新建 TCP、但节点一直命中，这里会报 100% 复用。
    /// </para>
    /// </summary>
    public const string PoolLeasesInstrumentName = "axiosnet.client.pool.leases";

    /// <summary>指标名：新建 origin 节点数（Counter）。与租约数一除即节点命中率，与驱逐数相减即当前节点数。</summary>
    public const string PoolEndpointsCreatedInstrumentName = "axiosnet.client.pool.endpoints.created";

    /// <summary>
    /// 指标名：驱逐 origin 节点数（Counter），带 <c>axiosnet.pool.eviction.reason</c>
    /// （<c>idle</c> / <c>capacity</c> / <c>manual</c> / <c>clear</c> / <c>dispose</c>）。
    /// </summary>
    public const string PoolEndpointsEvictedInstrumentName = "axiosnet.client.pool.endpoints.evicted";

    /// <summary>
    /// 指标名：当前活跃租约数（ObservableGauge，进程级水位）。
    /// <para>
    /// 与 <see cref="ActiveRequestsInstrumentName"/> 的区别是**正文下载期间仍然计入**：流式响应拿到响应头后
    /// 请求就算结束（在途数 -1），但租约要等正文流 <c>Dispose</c> 才归还 —— 「长下载占了多少并发额度」
    /// 只能看这个指标。
    /// </para>
    /// </summary>
    public const string ActiveLeasesInstrumentName = "axiosnet.client.pool.active_leases";

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

    internal static readonly ActivitySource Source;

    internal static readonly DiagnosticListener Listener;

    private static readonly Histogram<double> RequestDuration;

    private static readonly Counter<long> Retries;

    private static readonly Counter<long> PoolLeases;

    private static readonly Counter<long> PoolEndpointsCreated;

    private static readonly Counter<long> PoolEndpointsEvicted;

    /// <summary>在途请求数（全进程）：用 ObservableGauge 收集而不是每次请求调 instrument —— 见静态构造。</summary>
    private static long _activeRequests;

    /// <summary>活跃租约数（全进程，跨所有池）：正文下载期间仍然计入，见 <see cref="ActiveLeasesInstrumentName"/>。</summary>
    private static long _activeLeases;

    static AxiosTelemetry()
    {
        // 版本跟着程序集走：后端能据此区分「同一个源的不同版本」，不必手工维护常量。
        var version = typeof(AxiosTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        Source = new ActivitySource(ActivitySourceName, version);
        Listener = new DiagnosticListener(DiagnosticListenerName);

        var meter = new Meter(MeterName, version);
        RequestDuration = meter.CreateHistogram<double>(
            RequestDurationInstrumentName,
            unit: "s",
            description: "AxiosNet 逻辑请求耗时（含重试与退避等待），单位秒。");

        // net6.0 的 Metrics 没有 UpDownCounter（.NET 7 才加），而 Counter 只该单调递增。
        // 改成 ObservableGauge：进 / 出各一次 Interlocked，比每请求调一次 instrument 更便宜，
        // 代价是拿不到 method 这类标签 —— 在途数是「进程级水位」，本来也不适合按请求维度切。
        // 计的是「已成型、进了发送阶段」的请求：请求拦截器与 transformRequest 的准备阶段不算占用。
        meter.CreateObservableGauge(
            ActiveRequestsInstrumentName,
            () => Volatile.Read(ref _activeRequests),
            unit: "{request}",
            description: "已成型（URL 已拼好）并进入发送阶段、尚未结束的请求数。");

        Retries = meter.CreateCounter<long>(
            RetriesInstrumentName,
            unit: "{retry}",
            description: "按策略决定重试的次数（不含首次请求）。");

        // 池域指标：BCL 在 net6/7 上完全没有对应项，net8+ 的内置指标只覆盖 socket 层（连接数 / 排队 / 连接时长），
        // 「按 origin 复用的 Handler 节点」是库独有的语义层 —— 不重复，也不冲突。
        PoolLeases = meter.CreateCounter<long>(
            PoolLeasesInstrumentName,
            unit: "{lease}",
            description: "成功租用 origin 节点的次数；axiosnet.pool.endpoint.reused=false 表示本次新建了节点。");

        PoolEndpointsCreated = meter.CreateCounter<long>(
            PoolEndpointsCreatedInstrumentName,
            unit: "{endpoint}",
            description: "新建的 origin 节点数（Handler + HttpClient），即节点未命中的次数。");

        PoolEndpointsEvicted = meter.CreateCounter<long>(
            PoolEndpointsEvictedInstrumentName,
            unit: "{endpoint}",
            description: "被驱逐并释放的 origin 节点数，按 axiosnet.pool.eviction.reason 区分原因。");

        // 与在途请求数同一手法（net6.0 没有 UpDownCounter）：进 / 出各一次 Interlocked，收集时读。
        // 计的是「持有租约」的请求，含正文还没读完的流式响应 —— 这正是它相对 active_requests 的价值。
        meter.CreateObservableGauge(
            ActiveLeasesInstrumentName,
            () => Volatile.Read(ref _activeLeases),
            unit: "{lease}",
            description: "当前持有 origin 节点租约的请求数（含正文下载中的流式响应）。");
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

    /// <summary>
    /// 一次成功的租约：<c>pool.leases</c> +1（带 <c>reused</c> 标签），节点是新建时再记一条
    /// <c>pool.endpoints.created</c>。
    /// <para>
    /// 标签里只放主机 / 端口 / 布尔值 —— 池的基数天然被 <see cref="AxiosNet.Pooling.ConnectionPoolOptions.MaxEndpointCount"/>
    /// 框住，与请求指标的 <c>server.address</c> 同一口径。
    /// </para>
    /// <para>
    /// 池域指标<b>不受 <see cref="AxiosConfig.TelemetrySettings.Enabled"/> 约束</b>：它描述的是池这个资源
    /// （谁占了连接额度、谁在建节点），不是某一次请求的产出；健康检查照样会建节点，那一份成本就是真的。
    /// </para>
    /// </summary>
    internal static void RecordLease(string host, int port, bool reused)
    {
        if (!PoolLeases.Enabled)
        {
            return;
        }

        try
        {
            PoolLeases.Add(1, LeaseTags(host, port, reused));
        }
        catch
        {
            // 测量回调抛异常不能影响租用（见类注释）
        }
    }

    /// <summary>新建了一个 origin 节点：<c>pool.endpoints.created</c> +1。</summary>
    internal static void RecordEndpointCreated(string host, int port)
    {
        if (!PoolEndpointsCreated.Enabled)
        {
            return;
        }

        try
        {
            var tags = new TagList
            {
                { TagServerAddress, host },
                { TagServerPort, port }
            };
            PoolEndpointsCreated.Add(1, tags);
        }
        catch
        {
        }
    }

    /// <summary>驱逐了一个 origin 节点：<c>pool.endpoints.evicted</c> +1，带原因与目标。</summary>
    internal static void RecordEndpointEvicted(string reason, string host, int port)
    {
        if (!PoolEndpointsEvicted.Enabled)
        {
            return;
        }

        try
        {
            var tags = new TagList
            {
                { TagEvictionReason, reason },
                { TagServerAddress, host },
                { TagServerPort, port }
            };
            PoolEndpointsEvicted.Add(1, tags);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 把「这次租约是否复用了已有节点」写到当前 span 上。
    /// <para>
    /// 这里用 <see cref="Activity.Current"/> 是**有意的**，且必须先确认它就是本库的 span：
    /// 租用发生在 <c>SendCoreAsync</c> 里，那一层的参数里没有 span，为一个标签把 <see cref="Activity"/>
    /// 从 <c>RequestAsync</c> 一路传下来只会让三个私有方法的签名都多一个参数。
    /// 归属判定与 <see cref="RequestRetried"/> 同一口径 —— 调用方自己的 span 上不写库的标签。
    /// </para>
    /// </summary>
    internal static void TagPoolReuse(bool reused)
    {
        var activity = Activity.Current;
        if (activity is null || !ReferenceEquals(activity.Source, Source))
        {
            return;
        }

        try
        {
            // 重试时这个方法会被再次调用：第一次尝试建了节点（false）之后的尝试都是复用（true），
            // 直接覆盖会把「这个请求付了建节点的代价」这条最有用的信息冲掉。所以一旦写过就不再改
            // —— 重试路径上只多一次 GetTagItem（不分配），比「按 attempt 传参」划算。
            if (reused && activity.GetTagItem(TagEndpointReused) is not null)
            {
                return;
            }

            // 用 "true"/"false" 字面量而不是 bool：字面量是驻留字符串，不产生每请求的装箱分配。
            activity.SetTag(TagEndpointReused, reused ? "true" : "false");
        }
        catch
        {
        }
    }

    private static TagList LeaseTags(string host, int port, bool reused) => new()
    {
        { TagServerAddress, host },
        { TagServerPort, port },
        { TagEndpointReused, reused ? "true" : "false" }
    };

    /// <summary>
    /// 请求已成型（拦截器与 transformRequest 跑完、URL 已拼好）：开 span 并发出
    /// <see cref="RequestStartEventName"/>。没有监听者时返回 <c>null</c>，调用方不必判空也能一路传下去。
    /// </summary>
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
                // span 名按语义约定取「方法」（.NET 8 的 HttpClient 同样如此），URL 放在标签里：
                // 名称里带 URL 会让后端按 URL 分组成千上万个「操作」，反而看不出接口维度。
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

    /// <summary>
    /// 收掉 span（等价于 <see cref="Activity.Stop"/>）。
    /// <para>
    /// 单独包一层 try/catch 是保险而不是必需：实测 <c>Activity.Dispose()</c> 会吞掉
    /// <c>ActivityStopped</c> 回调抛出的异常（而 <c>ActivitySource.StartActivity</c> 不会 ——
    /// <c>ActivityStarted</c> 抛什么就往外抛什么，所以 <see cref="StartRequest"/> 里的 try 是必需的）。
    /// 它跑在请求的 finally 里，宁可多一层也不让「别人家的遥测代码写错了」变成「我的请求失败了」。
    /// </para>
    /// </summary>
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
        // 整个方法体都在 try 里（连取时间戳、拼 error.type 这类「看起来不会抛」的前置计算也一样）：
        // 它跑在请求的**成功路径**上，任何意外都不能变成「请求失败」。
        try
        {
            var elapsed = ElapsedSeconds(startTimestamp);

            // 4xx / 5xx 即使被 validateStatus 接受，在语义约定里也算「HTTP 层面的错误」：
            // span 标 Error、error.type 取状态码 —— 与失败路径（抛 AxiosException）口径一致。
            var errorType = response.Status >= 400
                ? response.Status.ToString(CultureInfo.InvariantCulture)
                : null;

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
                Emit(
                    RequestStopEventName,
                    new AxiosRequestStopEvent(config.Method, uri, response, TimeSpan.FromSeconds(elapsed)));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 请求失败结束：补状态码 / 错误码与异常事件，记录耗时指标，发出
    /// <see cref="RequestFailedEventName"/>。
    /// <para>
    /// <paramref name="uri"/> 为 <c>null</c> 表示请求还没成型（配置校验 / 拦截器就抛了）：
    /// 那不是一次 HTTP 操作，不记指标也不开事件，避免污染「接口耗时」的统计口径。
    /// </para>
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

        // 这个方法是在调用方的 catch 块里跑的：它自己抛异常会**顶掉原始异常**（连 throw; 都跑不到），
        // 所以连前置计算也一起包进 try。
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

                // error.type 与库错误码是两个维度，不能互相替代：有状态码时 error.type 取状态码（语义约定），
                // 「这次失败被归到哪一类」（ERR_NETWORK / ECONNABORTED / ERR_CANCELED / ERR_BAD_RESPONSE）
                // 就无处可写了。没有状态码时两者取值相同，但仍写一次 —— 后端不必按「有没有 status」分支。
                if (axiosException?.Code is string code)
                {
                    activity.SetTag(TagErrorCode, code);
                }

                activity.SetStatus(ActivityStatusCode.Error, exception.Message);

                // net6.0 没有 Activity.AddException（.NET 9 才有），按语义约定手工记一条 exception 事件：
                // 不记的话后端只知道「失败了」，看不到失败在哪一行。
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
                Emit(
                    RequestFailedEventName,
                    new AxiosRequestFailedEvent(
                        config.Method,
                        uri,
                        exception,
                        axiosException?.Code,
                        statusCode,
                        TimeSpan.FromSeconds(elapsed)));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 一次尝试失败后决定重试：重试计数 +1，在 span 上累加 <c>http.request.resend_count</c> 并记一条
    /// <c>retry</c> 事件，同时发出 <see cref="RetryEventName"/>。
    /// <para>
    /// span 的定位靠「<see cref="Activity.Current"/> 是不是本库的源」而不是「有没有 Activity」：
    /// 调用方可能自己开了一个 Activity，往别人的 span 上写重试次数是错的。
    /// </para>
    /// <para>
    /// 本方法<b>自己判 <see cref="AxiosConfig.TelemetrySettings.Enabled"/></b>（取自 <see cref="AxiosRetryContext.Config"/>）：
    /// 它由重试循环调用，不经过主路径那个 <c>telemetryEnabled</c> 分支。
    /// </para>
    /// </summary>
    internal static void RequestRetried(in AxiosRetryContext context, Uri uri)
    {
        try
        {
            // 重试这段观测在 SendWithRetryAsync 里，**不在**主路径的 telemetryEnabled 分支内，
            // 所以必须自己判一次开关：漏判的话，Telemetry.Enabled = false 的请求照样会
            // 往重试计数里加 1、照样发 Retry 事件（span 与耗时指标却都没有）—— 指标会被悄悄污染。
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

            if (hasMetric)
            {
                var tags = new TagList
                {
                    { TagMethod, method },
                    { TagServerAddress, HostOf(uri) },
                    { TagServerPort, uri.Port },
                    { TagErrorType, errorType }
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
    /// 发一条诊断事件。<b>调用前必须先用 <see cref="DiagnosticListener.IsEnabled(string)"/> 判定</b>：
    /// 载荷对象本身有分配，没人订阅时不该白造一个。
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "载荷是本库的公开事件类型（AxiosRequestStartEvent / AxiosRequestStopEvent / "
            + "AxiosRequestFailedEvent / AxiosRetryContext），订阅方直接按类型读属性、不做反射；"
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
    /// <c>url.full</c> 的取值：完整 URL，但**去掉 userinfo**（<c>https://user:pass@host/</c> 的凭据不该进 span ——
    /// span 会进第三方后端，而凭据在那里既没用也不安全）。
    /// <para>
    /// 查询串默认也不写（<paramref name="includeQuery"/> = <c>false</c>）：<c>?access_token=...</c> / 签名
    /// 同样是凭据，默认上报等于把它们送进第三方后端。需要排障时用 <c>Telemetry.CaptureQuery = true</c> 显式打开。
    /// </para>
    /// <para>
    /// 没有 userinfo 且要查询串时直接用 <see cref="Uri.AbsoluteUri"/>（零额外处理）；否则走 <c>GetComponents</c> 重建，
    /// 它返回的仍是「已转义」的形态（<c>UriFormat.UriEscaped</c>），不会二次编码。
    /// </para>
    /// </summary>
    private static string UrlForTelemetry(Uri uri, bool includeQuery)
    {
        if (uri.UserInfo.Length == 0 && includeQuery)
        {
            return uri.AbsoluteUri;
        }

        var components = includeQuery
            ? UriComponents.HttpRequestUrl
            : UriComponents.HttpRequestUrl & ~UriComponents.Query;

        return uri.GetComponents(components, UriFormat.UriEscaped);
    }

    /// <summary>
    /// 耗时直方图：标签与方法 / 主机 / 端口 + 状态码 + 错误类型（全是低基数维度）。
    /// <para>
    /// 先判 <see cref="Instrument.Enabled"/>（net6.0 就有）再组装标签：没有监听者时连
    /// <c>uri.IdnHost</c> 这个字符串都不必取 —— 指标是「无条件记录」的唯一一路，这个闸口是它零成本的前提。
    /// </para>
    /// <para>
    /// 注意 <see cref="Instrument.Enabled"/> <b>是单向的</b>：实测某个 <c>MeterListener</c> 打开它之后，
    /// 即使监听器已 <c>Dispose</c>，它仍返回 <c>true</c>（BCL 不回退）。也就是说「曾经有人订阅过」之后
    /// 每个请求都会继续付这份标签组装（实测 +152 B/请求），直到进程结束。这不值得额外处理：
    /// 进程内挂过监听器基本等于「要长期观测」，而为「监听器中途消失」再维护一层自己的计数只会更贵。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <c>error.type</c> 的取值：有 HTTP 状态码就用状态码（语义约定要求），否则用库错误码
    /// （<c>ERR_NETWORK</c> / <c>ECONNABORTED</c> / …，比异常类型名稳定），最后才退到异常类型名。
    /// </summary>
    private static string ErrorTypeOf(Exception exception, int? statusCode)
    {
        if (statusCode is int status)
        {
            return status.ToString(CultureInfo.InvariantCulture);
        }

        return (exception as AxiosException)?.Code ?? exception.GetType().Name;
    }

    /// <summary>
    /// <c>network.protocol.version</c> 的取值：语义约定只认 <c>1.0</c> / <c>1.1</c> / <c>2</c> / <c>3</c>，
    /// 而 <see cref="Version"/> 的 <c>ToString()</c> 给的是 <c>2.0</c> / <c>3.0</c>。
    /// </summary>
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
