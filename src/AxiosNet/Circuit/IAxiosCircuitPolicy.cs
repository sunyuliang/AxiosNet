namespace AxiosNet.Circuit;

/// <summary>
/// 熔断策略：决定一次尝试发不发得出去（<see cref="ShouldAllow"/>），以及把结果记到哪个维度（<see cref="Record"/>）。
/// 默认不熔断（<see cref="AxiosConfig.CircuitSettings.Policy"/> 为 <c>null</c> 时管线不走熔断分支）；实现应线程安全，状态维护自己保证。
/// </summary>
/// <example>
/// <code>
/// // 内置实现：5 次连续失败、或最近 20 次里失败率过半（至少 10 个样本）就熔断；
/// // 冷却 30 秒后进入半开，连续 3 次探测成功才全量放开
/// config.Circuit.Policy = new CircuitBreaker
/// {
///     FailureThreshold = 5,
///     WindowSize = 20,
///     MinimumSamples = 10,
///     FailureRateThreshold = 0.5,
///     BreakDuration = TimeSpan.FromSeconds(30),
///     RecoverySuccesses = 3
/// };
///
/// // 自定义策略：连续 3 次下游故障（IsFailure 口径，含网络错误与 5xx）就熔断，冷却期固定 10 秒
/// public sealed class BizCircuitPolicy : IAxiosCircuitPolicy
/// {
///     private int _failures;
///
///     public bool ShouldAllow(AxiosCircuitContext context) => Volatile.Read(ref _failures) &lt; 3;
///
///     public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.FromSeconds(10);
///
///     public void Record(AxiosCircuitContext context)
///     {
///         if (context.IsSuccess) Volatile.Write(ref _failures, 0);
///         else if (context.IsFailure) Interlocked.Increment(ref _failures);
///     }
/// }
/// </code>
/// </example>
public interface IAxiosCircuitPolicy
{
    /// <summary>
    /// 请求发出前的准入判定：返回 <c>false</c> 时本次尝试不会发出，直接以 <c>ERR_CIRCUIT_OPEN</c> 失败。
    /// 半开态的探测名额通常在这里领取，随后由一次 <see cref="Record"/> 归还；实现不应有阻塞式等待。
    /// </summary>
    bool ShouldAllow(AxiosCircuitContext context);

    /// <summary>
    /// 被拒绝时「还要等多久」（仅用于错误消息与 <see cref="AxiosException.RetryAfter"/>）；
    /// <see cref="TimeSpan.Zero"/> 表示说不准。只在 <see cref="ShouldAllow"/> 返回 <c>false</c> 之后调用。
    /// </summary>
    TimeSpan GetRetryAfter(AxiosCircuitContext context);

    /// <summary>
    /// 记录一次尝试的结论：按 <see cref="AxiosCircuitContext.IsSuccess"/> / <see cref="AxiosCircuitContext.IsFailure"/> 判定 ——
    /// 异常为 <c>null</c> 也可能是 5xx 被 <c>validateStatus</c> 放行（仍算下游故障），两者皆否时（取消 / 4xx / 请求从未发出）只归还名额。
    /// 放行之后必然调用一次（成功、失败、取消 / 4xx、以及「预算耗尽、请求从未发出」都会走到），半开态的探测名额靠它归还。
    /// </summary>
    void Record(AxiosCircuitContext context);
}
