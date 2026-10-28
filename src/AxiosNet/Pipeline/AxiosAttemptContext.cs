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
        CancellationToken attemptToken,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse,
        AxiosConfig.RetrySettings? retry,
        AxiosConfig.CircuitSettings? circuit,
        ConcurrencyGate? gate,
        IDisposable? slot)
    {
        Config = config;
        Uri = uri;
        AttemptNumber = attemptNumber;
        AttemptToken = attemptToken;
        ExternalToken = externalToken;
        DataType = dataType;
        Target = target;
        StreamResponse = streamResponse;
        Retry = retry;
        Circuit = circuit;
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
            externalToken,
            externalToken,
            dataType,
            target,
            config.ResponseType == ResponseType.Stream,
            retry,
            config.CircuitOrNull,
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
    /// 本次尝试的时间预算令牌：默认就是调用方的 <see cref="ExternalToken"/>。
    /// 真正的超时计时发生在终端内部，中间件自身耗时<b>不计入</b> <c>AxiosConfig.Timeout</c>。
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
        => new(Config, Uri, attemptNumber, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, _gate, Slot);

    /// <summary>派生：换本次尝试的令牌（对冲 / 备用端点给分支单独设预算时用）。</summary>
    public AxiosAttemptContext WithAttemptToken(CancellationToken attemptToken)
        => new(Config, Uri, AttemptNumber, attemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, _gate, Slot);

    /// <summary>
    /// 派生：填入本次尝试占到的并发槽位（闸门中间件用）。
    /// 填进去之后所有权就归终端 —— 失败路径它归还，流式成功它随正文转交给调用方。
    /// </summary>
    public AxiosAttemptContext WithSlot(IDisposable? slot)
        => new(Config, Uri, AttemptNumber, AttemptToken, ExternalToken, DataType, Target, StreamResponse,
            Retry, Circuit, _gate, slot);
}
