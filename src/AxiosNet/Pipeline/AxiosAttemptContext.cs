using AxiosNet.Internal;

namespace AxiosNet.Pipeline;

/// <summary>
/// 一次尝试的上下文：<b>不可变结构体，按值传递</b>，逐次尝试零分配。
/// 中间件只能用 <c>WithXxx</c> 派生副本，改不了上游看到的那一份 —— 这是刻意的：
/// 生效配置在阶段 4b 已校验完毕，尝试阶段再让中间件改配置就是绕过护栏。
/// </summary>
/// <remarks>
/// 用结构体而不是类的两个硬理由：①并发类中间件（对冲、备用端点）需要按分支拷贝上下文，
/// 结构体拷贝免费，类要么深拷贝要么共享可变状态；②<see cref="AxiosAttemptHandler"/> 用
/// <c>in</c> 参数接不住 async 状态机（<c>in</c> 不能被异步方法捕获），只能按值传。
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
        IDisposable? slot)
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
    }

    private readonly ConcurrencyGate? _gate;

    /// <summary>
    /// 唯一创建入口：派生项（流式标记 / 重试与熔断配置）在这里一次算清，
    /// 尝试过程中不再重算 —— 它们是「一次请求」的属性，不是「一次尝试」的。
    /// 正文可重放性不在这里检查：只有管线里真有重试中间件时才需要（见 <see cref="Attempt.Retry"/>）。
    /// </summary>
    internal static AxiosAttemptContext From(
        AxiosConfig config,
        Uri uri,
        Type? dataType,
        AxiosResponse? target,
        ConcurrencyGate? gate)
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
            null);
    }

    /// <summary>本次请求的生效配置（合并 + 拦截器之后的最终形态）。</summary>
    public AxiosConfig Config { get; }

    /// <summary>最终 URL：错误上下文、熔断计数维度与遥测标签都用它。</summary>
    public Uri Uri { get; }

    /// <summary>
    /// 第几次尝试，从 <c>1</c> 开始。只有重试中间件会推进它 ——
    /// 放在重试之外的中间件恒为 <c>1</c>，放在重试之内的中间件每次尝试各见一个值。
    /// </summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// 第几个端点，从 <c>0</c> 开始：<c>0</c> = 请求自己的地址，<c>n</c> = 端点表里第 n 个备用端点。
    /// 只有备用端点中间件会推进它，所以「一次尝试」可能出现多条编号相同、端点序号不同的事件。
    /// </summary>
    public int EndpointIndex { get; }

    /// <summary>
    /// 本次尝试的令牌：默认就是调用方的 <see cref="ExternalToken"/>，对冲给每条分支各换一份。
    /// 并发闸门的排队与终端的超时计时都用它，因此被放弃的尝试（对冲的败者）能被立刻收掉；
    /// 中间件自身耗时<b>不计入</b> <c>AxiosConfig.Timeout</c>。
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

    /// <summary>重试配置：<c>null</c> = 不重试（默认）。</summary>
    public AxiosConfig.RetrySettings? Retry { get; }

    /// <summary>熔断配置：<c>null</c> = 不熔断（默认）。</summary>
    public AxiosConfig.CircuitSettings? Circuit { get; }

    /// <summary>对冲配置：<c>null</c> = 未配置（默认）；<see cref="AxiosConfig.HedgingSettings.MaxHedges"/> 为 0 时同样不对冲。</summary>
    public AxiosConfig.HedgingSettings? Hedging { get; }

    /// <summary>备用端点配置：<c>null</c> = 未配置（默认）；端点表为空时同样不换端点。</summary>
    public AxiosConfig.FailoverSettings? Failover { get; }

    /// <summary>并发闸门：<c>null</c> = 本次不限并发。</summary>
    internal ConcurrencyGate? Gate => _gate;

    /// <summary>
    /// 本次尝试已占到的并发槽位：由闸门中间件填入，终端负责在失败路径归还、
    /// 在流式成功路径接管（转交给 <see cref="AxiosAttemptResult.StreamOwner"/>）。
    /// 没有闸门时为 <c>null</c>。
    /// </summary>
    public IDisposable? Slot { get; }

    /// <summary>派生：换一个尝试序号（重试中间件推进尝试时用）。</summary>
    public AxiosAttemptContext WithAttemptNumber(int attemptNumber)
        => new(Config, Uri, attemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot);

    /// <summary>派生：换本次尝试的令牌（对冲给每条分支单独设令牌时用）。</summary>
    public AxiosAttemptContext WithAttemptToken(CancellationToken attemptToken)
        => new(Config, Uri, AttemptNumber, EndpointIndex, attemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot);

    /// <summary>
    /// 派生：换本次尝试的生效配置（对冲给每条分支一份隔离副本时用）。
    /// 子设置也从新配置重新取，不与旧上下文混用 —— 否则配置换了、生效的策略还是上一份。
    /// 内部入口：配置在阶段 4b 已校验完毕，对外不开放「尝试阶段换配置」这条路。
    /// </summary>
    internal AxiosAttemptContext WithConfig(AxiosConfig config)
        => new(config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            config.RetryOrNull, config.CircuitOrNull, config.HedgingOrNull, config.FailoverOrNull, _gate, Slot);

    /// <summary>
    /// 派生：摘掉调用方预造的响应对象（对冲给每条分支一份自己的响应时用）。
    /// 预造对象只有一个实例，多条分支并发就地填充会互相覆盖。
    /// </summary>
    internal AxiosAttemptContext WithoutTarget()
        => new(Config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, null, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot);

    /// <summary>
    /// 派生：换目标 URL（备用端点中间件切镜像时用）。
    /// <see cref="Uri"/> 同时参与连接租约、熔断计数维度与遥测标签，换端点必须连它一起换，只改请求消息不够。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> 为 <c>null</c>。</exception>
    public AxiosAttemptContext WithUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new(Config, uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, Slot);
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
            Retry, Circuit, Hedging, Failover, _gate, Slot);
    }

    /// <summary>
    /// 派生：填入本次尝试占到的并发槽位（闸门中间件用）。
    /// 填进去之后所有权就归终端 —— 失败路径它归还，流式成功它随正文转交给调用方。
    /// </summary>
    public AxiosAttemptContext WithSlot(IDisposable? slot)
        => new(Config, Uri, AttemptNumber, EndpointIndex, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, Hedging, Failover, _gate, slot);
}
