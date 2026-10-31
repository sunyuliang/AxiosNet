using AxiosNet.Internal;

namespace AxiosNet.Pipeline;

/// <summary>
/// 一次尝试的上下文：<b>不可变结构体，按值传递</b>，逐次尝试零分配。
/// 中间件只能用 <c>WithXxx</c> 派生副本，改不了上游看到的那一份。
/// </summary>
/// <remarks>
/// 用结构体而不是类：并发类中间件（对冲、备用端点）需要按分支拷贝上下文，且 <see cref="AxiosAttemptHandler"/>
/// 的 async 状态机无法捕获 <c>in</c> 参数。
/// </remarks>
public readonly struct AxiosAttemptContext
{
    private AxiosAttemptContext(
        AxiosConfig config,
        Uri uri,
        int attemptNumber,
        int endpointIndex,
        CancellationToken attemptToken,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse,
        AxiosConfig.RetrySettings? retry,
        AxiosConfig.CircuitSettings? circuit,
        AxiosConfig.HedgingSettings? hedging,
        AxiosConfig.FailoverSettings? failover,
        ConcurrencyGate? gate,
        IDisposable? slot,
        long deadlineTimestamp,
        FailoverHealth? failoverHealth)
    {
        Config = config;
        Uri = uri;
        AttemptNumber = attemptNumber;
        EndpointIndex = endpointIndex;
        AttemptToken = attemptToken;
        ExternalToken = externalToken;
        DataType = dataType;
        Target = target;
        StreamResponse = streamResponse;
        Retry = retry;
        Circuit = circuit;
        Hedging = hedging;
        Failover = failover;
        _gate = gate;
        Slot = slot;
        _deadlineTimestamp = deadlineTimestamp;
        _failoverHealth = failoverHealth;
    }

    private readonly ConcurrencyGate? _gate;

    /// <summary>备用端点健康表（客户端级共享状态；注入自定义 Handler 时同样存在，只是没有连接池可换）。</summary>
    private readonly FailoverHealth? _failoverHealth;

    /// <summary>总预算的绝对时间戳（<see cref="TotalTimeoutBudget.None"/> = 没设），一次请求只算一次。</summary>
    private readonly long _deadlineTimestamp;

    /// <summary>
    /// 唯一创建入口：流式标记与重试 / 熔断 / 对冲 / 端点配置在这里一次算清，尝试过程中不再重算。
    /// 正文可重放性不在这里检查（见 <see cref="Attempt.Retry"/>）。
    /// </summary>
    internal static AxiosAttemptContext From(
        AxiosConfig config,
        Uri uri,
        Type? dataType,
        AxiosResponse? target,
        ConcurrencyGate? gate,
        long deadlineTimestamp,
        FailoverHealth? failoverHealth)
    {
        var retry = config.RetryOrNull;
        var externalToken = config.CancellationToken;

        return new AxiosAttemptContext(
            config,
            uri,
            1,
            0,
            externalToken,
            externalToken,
            dataType,
            target,
            config.ResponseType == ResponseType.Stream,
            retry,
            config.CircuitOrNull,
            config.HedgingOrNull,
            config.FailoverOrNull,
            gate,
            null,
            deadlineTimestamp,
            failoverHealth);
    }

    /// <summary>本次请求的生效配置（合并 + 拦截器之后的最终形态）。</summary>
    public AxiosConfig Config { get; }

    /// <summary>最终 URL：错误上下文、熔断计数维度与遥测标签都用它。</summary>
    public Uri Uri { get; }

    /// <summary>
    /// 第几次尝试，从 <c>1</c> 开始。只有重试中间件会推进它。
    /// </summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// 第几个端点，从 <c>0</c> 开始：<c>0</c> = 请求自己的地址，<c>n</c> = 端点表里第 n 个备用端点。
    /// 只有备用端点中间件会推进它。
    /// </summary>
    public int EndpointIndex { get; }

    /// <summary>
    /// 本次尝试的令牌：默认同 <see cref="ExternalToken"/>，对冲给每条分支各换一份。
    /// 并发闸门排队与终端超时计时都用它；中间件自身耗时不计入 <c>AxiosConfig.Timeout</c>，但计入
    /// <see cref="AxiosConfig.TotalTimeout"/>。
    /// </summary>
    public CancellationToken AttemptToken { get; }

    /// <summary>调用方令牌：贯穿所有尝试与退避等待。</summary>
    public CancellationToken ExternalToken { get; }

    /// <summary>泛型入口的目标类型（非泛型入口为 <c>null</c>），决定反序列化成什么。</summary>
    public Type? DataType { get; }

    /// <summary>调用方预造的响应对象：管线就地填充，省掉一次类型转换与再分配。</summary>
    public AxiosResponse? Target { get; }

    /// <summary>是否流式响应（<c>ResponseType == Stream</c>）。</summary>
    public bool StreamResponse { get; }

    /// <summary>重试配置：<c>null</c> = 不重试（默认）；设了但 <see cref="AxiosConfig.RetrySettings.Policy"/> 为 <c>null</c> 或 <c>MaxRetries &lt;= 0</c> 时同样不重试。</summary>
    public AxiosConfig.RetrySettings? Retry { get; }

    /// <summary>熔断配置：<c>null</c> = 不熔断（默认）；设了但 <see cref="AxiosConfig.CircuitSettings.Policy"/> 为 <c>null</c> 时同样不熔断。</summary>
    public AxiosConfig.CircuitSettings? Circuit { get; }

    /// <summary>对冲配置：<c>null</c> = 未配置（默认）；<see cref="AxiosConfig.HedgingSettings.MaxHedges"/> 为 0 时同样不对冲。</summary>
    public AxiosConfig.HedgingSettings? Hedging { get; }

    /// <summary>备用端点配置：<c>null</c> = 未配置（默认）；端点表为空时同样不换端点。</summary>
    public AxiosConfig.FailoverSettings? Failover { get; }

    /// <summary>并发闸门：<c>null</c> = 本次不限并发。</summary>
    internal ConcurrencyGate? Gate => _gate;

    /// <summary>备用端点的健康表（客户端级共享，跨请求记忆；<c>null</c> 表示不记忆）。</summary>
    internal FailoverHealth? FailoverHealth => _failoverHealth;

    /// <summary>
    /// 本次尝试已占到的并发槽位：由闸门中间件填入，终端负责在失败路径归还、
    /// 在流式成功路径接管（转交给 <see cref="AxiosAttemptResult.StreamOwner"/>）。
    /// 没有闸门时为 <c>null</c>。
    /// </summary>
    public IDisposable? Slot { get; }

    /// <summary>距总预算耗尽还剩多久；<c>null</c> = 没设总预算，<see cref="TimeSpan.Zero"/> = 已耗尽。</summary>
    internal TimeSpan? RemainingTotalTimeout => TotalTimeoutBudget.Remaining(_deadlineTimestamp);

    /// <summary>派生：换一个尝试序号（重试中间件推进尝试时用）。</summary>
    public AxiosAttemptContext WithAttemptNumber(int attemptNumber)
        => new(Config, Uri, attemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot, _deadlineTimestamp, _failoverHealth);

    /// <summary>派生：换本次尝试的令牌（对冲给每条分支单独设令牌时用）。</summary>
    public AxiosAttemptContext WithAttemptToken(CancellationToken attemptToken)
        => new(Config, Uri, AttemptNumber, EndpointIndex, attemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot, _deadlineTimestamp, _failoverHealth);

    /// <summary>
    /// 派生：换本次尝试的生效配置（对冲给每条分支一份隔离副本时用），子设置一并从新配置取。
    /// 内部入口：对外不开放「尝试阶段换配置」这条路。
    /// </summary>
    internal AxiosAttemptContext WithConfig(AxiosConfig config)
        => new(config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            config.RetryOrNull, config.CircuitOrNull, config.HedgingOrNull, config.FailoverOrNull, _gate, Slot, _deadlineTimestamp, _failoverHealth);

    /// <summary>
    /// 派生：摘掉调用方预造的响应对象（对冲给每条分支一份自己的响应时用）—— 预造对象只有一个实例，
    /// 多条分支并发就地填充会互相覆盖。
    /// </summary>
    internal AxiosAttemptContext WithoutTarget()
        => new(Config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, null, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot, _deadlineTimestamp, _failoverHealth);

    /// <summary>
    /// 派生：换目标 URL（备用端点中间件切镜像时用）。
    /// <see cref="Uri"/> 同时参与连接租约、熔断计数维度与遥测标签，换端点必须连它一起换。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> 为 <c>null</c>。</exception>
    public AxiosAttemptContext WithUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new(Config, uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot, _deadlineTimestamp, _failoverHealth);
    }

    /// <summary>派生：换端点序号（备用端点中间件切到第 n 个备用端点时用，配合 <see cref="WithUri"/>）。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="endpointIndex"/> 为负。</exception>
    public AxiosAttemptContext WithEndpointIndex(int endpointIndex)
    {
        if (endpointIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex), endpointIndex, "端点序号不能为负。");
        }

        return new(Config, Uri, AttemptNumber, endpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot, _deadlineTimestamp, _failoverHealth);
    }

    /// <summary>
    /// 派生：填入本次尝试占到的并发槽位（闸门中间件用）。
    /// 填进去之后所有权归终端：失败路径它归还，流式成功它随正文转交给调用方。
    /// </summary>
    public AxiosAttemptContext WithSlot(IDisposable? slot)
        => new(Config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, slot, _deadlineTimestamp, _failoverHealth);
}
