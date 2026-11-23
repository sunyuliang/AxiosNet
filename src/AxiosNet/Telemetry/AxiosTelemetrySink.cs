using AxiosNet.Circuit;
using AxiosNet.Pipeline;
using AxiosNet.Retry;

namespace AxiosNet.Telemetry;

/// <summary>一次请求的观测句柄：由 <see cref="IAxiosTelemetrySink.StartRequest"/> 交出，后续观测按它派发回同一实现。</summary>
/// <remarks>结构体，默认值即「本次没有观测」（<see cref="IsActive"/> 为 <c>false</c>）；<see cref="State"/> 是各实现自己的载体。</remarks>
public readonly struct AxiosRequestTrace
{
    /// <summary>创建句柄的实现；<c>null</c> = 没有观测。</summary>
    private readonly IAxiosTelemetrySink? _sink;

    /// <summary>用指定实现与它自己的载体创建句柄。</summary>
    /// <param name="sink">创建它的实现。</param>
    /// <param name="state">实现自己的载体（内置实现是 <see cref="System.Diagnostics.Activity"/>）。</param>
    public AxiosRequestTrace(IAxiosTelemetrySink sink, object? state)
    {
        _sink = sink;
        State = state;
    }

    /// <summary>实现自己的载体；<see cref="IsActive"/> 为 <c>false</c> 时无意义。</summary>
    public object? State { get; }

    /// <summary>本次请求是否真的在观测（<c>false</c> = 所有观测调用都是空操作）。</summary>
    public bool IsActive => _sink is not null;

    /// <summary>告知实现本次请求的正文大小（已知长度时才调）。</summary>
    /// <param name="size">正文字节数。</param>
    public void SetRequestBodySize(long? size) => _sink?.SetRequestBodySize(this, size);

    /// <summary>结束本次请求的观测。重复调用由实现自己保证幂等。</summary>
    public void Stop() => _sink?.StopRequest(this);
}

/// <summary>库的观测实现：链路、指标与诊断事件全部经它出去，<see cref="AxiosTelemetry.Sink"/> 可整套替换。</summary>
/// <remarks>
/// 内置实现写 <see cref="System.Diagnostics.ActivitySource"/> / <see cref="System.Diagnostics.Metrics.Meter"/> /
/// <see cref="System.Diagnostics.DiagnosticListener"/>；换实现时内置那套整套停用，不是叠加。实现会在并发线程上被调用，
/// 须线程安全且不应抛异常（派发层会吞掉）。<see cref="IsEnabled"/> 只管请求级生命周期，其余事件与水位不受它管辖。
/// </remarks>
public interface IAxiosTelemetrySink
{
    /// <summary>本次请求要不要观测；只作用于请求级生命周期（开 / 收句柄与成败上报），事件类不看这个开关。</summary>
    /// <param name="config">本次请求的生效配置；<c>null</c> 表示「没表态」，按默认处理。</param>
    /// <returns><c>true</c> = 会为本次请求开观测句柄并上报成败。</returns>
    bool IsEnabled(AxiosConfig? config);

    /// <summary>请求已成型（拦截器与 transformRequest 跑完）：开始观测。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="uri">最终 URL（已拼好）。</param>
    /// <returns>观测句柄；不观测时返回 <c>default</c>。</returns>
    AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri);

    /// <summary>请求结束（无论成败）：收尾 <see cref="StartRequest"/> 开的观测。</summary>
    /// <param name="trace">本次请求的观测句柄。</param>
    void StopRequest(in AxiosRequestTrace trace);

    /// <summary>记下请求体大小（已知长度时才调）。</summary>
    /// <param name="trace">本次请求的观测句柄。</param>
    /// <param name="size">正文字节数。</param>
    void SetRequestBodySize(in AxiosRequestTrace trace, long? size);

    /// <summary>请求进入发送管线：在途数 +1（与 <see cref="ExitRequest"/> 必须成对）。</summary>
    /// <remarks>抛异常只让本次请求不再上报请求级观测，不影响请求结果与水位。</remarks>
    void EnterRequest();

    /// <summary>请求离开发送管线：在途数 -1。</summary>
    void ExitRequest();

    /// <summary>连接租约生效：活跃租约 +1（与 <see cref="ExitLease"/> 必须成对）。</summary>
    void EnterLease();

    /// <summary>连接租约归还：活跃租约 -1。</summary>
    void ExitLease();

    /// <summary>建成一条连接池：存活池数 +1（与 <see cref="ExitPool"/> 必须成对）。</summary>
    void EnterPool();

    /// <summary>连接池被释放：存活池数 -1。</summary>
    void ExitPool();

    /// <summary>一次成功的连接租用。</summary>
    /// <param name="reused">是否复用了已有节点（<c>false</c> = 本次新建）。</param>
    void RecordLease(bool reused);

    /// <summary>新建了一个 origin 节点。</summary>
    void RecordEndpointCreated();

    /// <summary>驱逐了一个 origin 节点。</summary>
    /// <param name="reason">驱逐原因（<c>idle</c> / <c>capacity</c> / <c>manual</c> / <c>clear</c> / <c>cookies</c> / <c>dispose</c>）。</param>
    void RecordEndpointEvicted(string reason);

    /// <summary>把「这次租约是否复用已有节点」写到当前链路节点上。</summary>
    /// <param name="reused">是否复用。</param>
    void TagPoolReuse(bool reused);

    /// <summary>熔断跟踪表已满、本次按闭合态直通（该维度不记账）。</summary>
    void CircuitUntrackedHost();

    /// <summary>请求成功结束。</summary>
    /// <remarks>派给本次请求开始时认下的那个实现（不是「此刻的 <see cref="AxiosTelemetry.Sink"/>」），因为它持有 <paramref name="trace"/> 的载体。</remarks>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="uri">最终 URL。</param>
    /// <param name="response">交付给调用方的响应。</param>
    /// <param name="startTimestamp">起点（<c>AxiosTelemetry.Timestamp</c>）。</param>
    /// <param name="trace">本次请求的观测句柄。</param>
    void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace);

    /// <summary>请求失败结束。配置或 URL 未成型时对应参数为 <c>null</c>；与 <see cref="RequestSucceeded"/> 同一口径，派给请求开头认下的实现。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="uri">最终 URL。</param>
    /// <param name="exception">抛给调用方的异常。</param>
    /// <param name="startTimestamp">起点（<c>AxiosTelemetry.Timestamp</c>）。</param>
    /// <param name="trace">本次请求的观测句柄。</param>
    void RequestFailed(AxiosConfig? config, Uri? uri, Exception exception, long startTimestamp, in AxiosRequestTrace trace);

    /// <summary>请求被并发闸门拒绝（未发出）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="reason">拒绝原因（<c>queue_full</c> / <c>queue_timeout</c>）。</param>
    void GateRejected(AxiosConfig config, string reason);

    /// <summary>一次失败尝试后决定重试。</summary>
    /// <param name="context">重试上下文（尝试序号、原因、退避时长…）。</param>
    /// <param name="uri">本次尝试的目标地址。</param>
    void RequestRetried(in AxiosRetryContext context, Uri uri);

    /// <summary>共享重试预算不足，放弃重试（请求未重发）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    void RetryBudgetRefused(AxiosConfig? config);

    /// <summary>缓存命中（从缓存交付，请求未发出）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    void CacheHit(AxiosConfig? config);

    /// <summary>缓存未命中（随后会真实发送）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    void CacheMiss(AxiosConfig? config);

    /// <summary>落库写入完成一条缓存条目。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    void CacheStored(AxiosConfig? config);

    /// <summary>落库失败（存储抛错 / 写超时 / 总预算已耗尽）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    void CacheStoreFailed(AxiosConfig? config);

    /// <summary>条件请求拿到 304：正文来自缓存，请求确实发出去了。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="trace">本次请求的观测句柄。</param>
    void CacheRevalidated(AxiosConfig? config, in AxiosRequestTrace trace);

    /// <summary>是否要收尝试级事件（只有链上装了审计中间件时才问）。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <returns><c>true</c> = 值得为每条尝试建事件载荷。</returns>
    bool IsAttemptAuditEnabled(AxiosConfig? config);

    /// <summary>一次尝试成功。</summary>
    /// <param name="context">本次尝试的上下文。</param>
    /// <param name="status">响应状态码。</param>
    /// <param name="startTimestamp">本次尝试的起点（<c>AxiosTelemetry.Timestamp</c>）。</param>
    void AttemptSucceeded(in AxiosAttemptContext context, int? status, long startTimestamp);

    /// <summary>一次尝试失败。</summary>
    /// <param name="context">本次尝试的上下文。</param>
    /// <param name="exception">本次尝试抛出的异常。</param>
    /// <param name="startTimestamp">本次尝试的起点（<c>AxiosTelemetry.Timestamp</c>）。</param>
    void AttemptFailed(in AxiosAttemptContext context, Exception exception, long startTimestamp);
}

/// <summary><see cref="IAxiosTelemetrySink"/> 的全空实现：派生它只覆写关心的几个方法。</summary>
/// <remarks>默认「不观测」（<see cref="IsEnabled"/> 返回 <c>false</c>）：覆写请求级方法时须同时让 <see cref="IsEnabled"/> 返回 <c>true</c>；事件类方法不受该开关管辖。</remarks>
public abstract class AxiosTelemetrySink : IAxiosTelemetrySink
{
    /// <summary>什么都不做的实现：设成 <see cref="AxiosTelemetry.Sink"/> 即彻底关掉内置观测。</summary>
    public static AxiosTelemetrySink Off { get; } = new OffSink();

    /// <inheritdoc />
    public virtual bool IsEnabled(AxiosConfig? config) => false;

    /// <inheritdoc />
    public virtual AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri) => default;

    /// <inheritdoc />
    public virtual void StopRequest(in AxiosRequestTrace trace)
    {
    }

    /// <inheritdoc />
    public virtual void SetRequestBodySize(in AxiosRequestTrace trace, long? size)
    {
    }

    /// <inheritdoc />
    public virtual void EnterRequest()
    {
    }

    /// <inheritdoc />
    public virtual void ExitRequest()
    {
    }

    /// <inheritdoc />
    public virtual void EnterLease()
    {
    }

    /// <inheritdoc />
    public virtual void ExitLease()
    {
    }

    /// <inheritdoc />
    public virtual void EnterPool()
    {
    }

    /// <inheritdoc />
    public virtual void ExitPool()
    {
    }

    /// <inheritdoc />
    public virtual void RecordLease(bool reused)
    {
    }

    /// <inheritdoc />
    public virtual void RecordEndpointCreated()
    {
    }

    /// <inheritdoc />
    public virtual void RecordEndpointEvicted(string reason)
    {
    }

    /// <inheritdoc />
    public virtual void TagPoolReuse(bool reused)
    {
    }

    /// <inheritdoc />
    public virtual void CircuitUntrackedHost()
    {
    }

    /// <inheritdoc />
    public virtual void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
    {
    }

    /// <inheritdoc />
    public virtual void RequestFailed(AxiosConfig? config, Uri? uri, Exception exception, long startTimestamp, in AxiosRequestTrace trace)
    {
    }

    /// <inheritdoc />
    public virtual void GateRejected(AxiosConfig config, string reason)
    {
    }

    /// <inheritdoc />
    public virtual void RequestRetried(in AxiosRetryContext context, Uri uri)
    {
    }

    /// <inheritdoc />
    public virtual void RetryBudgetRefused(AxiosConfig? config)
    {
    }

    /// <inheritdoc />
    public virtual void CacheHit(AxiosConfig? config)
    {
    }

    /// <inheritdoc />
    public virtual void CacheMiss(AxiosConfig? config)
    {
    }

    /// <inheritdoc />
    public virtual void CacheStored(AxiosConfig? config)
    {
    }

    /// <inheritdoc />
    public virtual void CacheStoreFailed(AxiosConfig? config)
    {
    }

    /// <inheritdoc />
    public virtual void CacheRevalidated(AxiosConfig? config, in AxiosRequestTrace trace)
    {
    }

    /// <inheritdoc />
    public virtual bool IsAttemptAuditEnabled(AxiosConfig? config) => false;

    /// <inheritdoc />
    public virtual void AttemptSucceeded(in AxiosAttemptContext context, int? status, long startTimestamp)
    {
    }

    /// <inheritdoc />
    public virtual void AttemptFailed(in AxiosAttemptContext context, Exception exception, long startTimestamp)
    {
    }

    /// <summary>彻底关掉观测的实现：先在门口返回 <c>false</c>，即使被调用也什么都不做。</summary>
    private sealed class OffSink : AxiosTelemetrySink
    {
    }
}

/// <summary>内置实现：把每个观测点转交给 <see cref="AxiosTelemetry"/> 里的静态实现（ActivitySource / Meter / DiagnosticListener）。</summary>
/// <remarks><see cref="IAxiosTelemetrySink"/> 与存量静态实现之间的适配层；静态字段留在 <see cref="AxiosTelemetry"/> 上，本类型为单例。</remarks>
internal sealed class BuiltInTelemetrySink : AxiosTelemetrySink
{
    /// <summary>唯一样本：指标与水位计数都在静态字段上，多实例没有意义。</summary>
    internal static readonly BuiltInTelemetrySink Instance = new();

    private BuiltInTelemetrySink()
    {
    }

    /// <inheritdoc />
    public override bool IsEnabled(AxiosConfig? config) => AxiosTelemetry.IsEnabled(config);

    /// <inheritdoc />
    public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
    {
        var activity = AxiosTelemetry.StartRequest(config, uri);
        return activity is null ? default : new AxiosRequestTrace(this, activity);
    }

    /// <inheritdoc />
    public override void StopRequest(in AxiosRequestTrace trace) => AxiosTelemetry.StopRequest(trace.State as System.Diagnostics.Activity);

    /// <inheritdoc />
    public override void SetRequestBodySize(in AxiosRequestTrace trace, long? size)
        => AxiosTelemetry.SetRequestBodySize(trace.State as System.Diagnostics.Activity, size);

    /// <inheritdoc />
    public override void EnterRequest() => AxiosTelemetry.EnterRequest();

    /// <inheritdoc />
    public override void ExitRequest() => AxiosTelemetry.ExitRequest();

    /// <inheritdoc />
    public override void EnterLease() => AxiosTelemetry.EnterLease();

    /// <inheritdoc />
    public override void ExitLease() => AxiosTelemetry.ExitLease();

    /// <inheritdoc />
    public override void EnterPool() => AxiosTelemetry.EnterPool();

    /// <inheritdoc />
    public override void ExitPool() => AxiosTelemetry.ExitPool();

    /// <inheritdoc />
    public override void RecordLease(bool reused) => AxiosTelemetry.RecordLease(reused);

    /// <inheritdoc />
    public override void RecordEndpointCreated() => AxiosTelemetry.RecordEndpointCreated();

    /// <inheritdoc />
    public override void RecordEndpointEvicted(string reason) => AxiosTelemetry.RecordEndpointEvicted(reason);

    /// <inheritdoc />
    public override void TagPoolReuse(bool reused) => AxiosTelemetry.TagPoolReuse(reused);

    /// <inheritdoc />
    public override void CircuitUntrackedHost() => AxiosTelemetry.CircuitUntrackedHost();

    /// <inheritdoc />
    public override void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
        => AxiosTelemetry.RequestSucceeded(config, uri, response, startTimestamp, trace.State as System.Diagnostics.Activity);

    /// <inheritdoc />
    public override void RequestFailed(AxiosConfig? config, Uri? uri, Exception exception, long startTimestamp, in AxiosRequestTrace trace)
        => AxiosTelemetry.RequestFailed(config, uri, exception, startTimestamp, trace.State as System.Diagnostics.Activity);

    /// <inheritdoc />
    public override void GateRejected(AxiosConfig config, string reason) => AxiosTelemetry.GateRejected(config, reason);

    /// <inheritdoc />
    public override void RequestRetried(in AxiosRetryContext context, Uri uri) => AxiosTelemetry.RequestRetried(context, uri);

    /// <inheritdoc />
    public override void RetryBudgetRefused(AxiosConfig? config) => AxiosTelemetry.RetryBudgetRefused(config);

    /// <inheritdoc />
    public override void CacheHit(AxiosConfig? config) => AxiosTelemetry.CacheHit(config);

    /// <inheritdoc />
    public override void CacheMiss(AxiosConfig? config) => AxiosTelemetry.CacheMiss(config);

    /// <inheritdoc />
    public override void CacheStored(AxiosConfig? config) => AxiosTelemetry.CacheStored(config);

    /// <inheritdoc />
    public override void CacheStoreFailed(AxiosConfig? config) => AxiosTelemetry.CacheStoreFailed(config);

    /// <inheritdoc />
    public override void CacheRevalidated(AxiosConfig? config, in AxiosRequestTrace trace)
        => AxiosTelemetry.CacheRevalidated(config, trace.State as System.Diagnostics.Activity);

    /// <inheritdoc />
    public override bool IsAttemptAuditEnabled(AxiosConfig? config) => AxiosTelemetry.IsAttemptAuditEnabled(config);

    /// <inheritdoc />
    public override void AttemptSucceeded(in AxiosAttemptContext context, int? status, long startTimestamp)
        => AxiosTelemetry.AttemptSucceeded(context, status, startTimestamp);

    /// <inheritdoc />
    public override void AttemptFailed(in AxiosAttemptContext context, Exception exception, long startTimestamp)
        => AxiosTelemetry.AttemptFailed(context, exception, startTimestamp);
}
