namespace AxiosNet.Circuit;

/// <summary>
/// 熔断策略：决定一次尝试发不发得出去（<see cref="ShouldAllow"/>），并把结果记到对应维度（<see cref="Record"/>）。
/// 默认不熔断；实现须线程安全。
/// </summary>
/// <example>
/// <code>
/// config.Resilience.Circuit.Policy = new CircuitBreaker
/// {
///     FailureThreshold = 5,
///     WindowSize = 20,
///     MinimumSamples = 10,
///     FailureRateThreshold = 0.5,
///     BreakDuration = TimeSpan.FromSeconds(30),
///     RecoverySuccesses = 3
/// };
///
/// // 自定义策略：实现 ShouldAllow / GetRetryAfter / Record 三个成员
/// </code>
/// </example>
public interface IAxiosCircuitPolicy
{
    /// <summary>请求发出前的准入判定：返回 <c>false</c> 时本次尝试不发出，以 <c>ERR_CIRCUIT_OPEN</c> 失败；半开探测名额在此领取、由一次 <see cref="Record"/> 归还。</summary>
    bool ShouldAllow(AxiosCircuitContext context);

    /// <summary>被拒绝时还要等多久（用于错误消息与 <see cref="AxiosException.RetryAfter"/>）；<see cref="TimeSpan.Zero"/> 表示说不准。仅在 <see cref="ShouldAllow"/> 返回 <c>false</c> 后调用。</summary>
    TimeSpan GetRetryAfter(AxiosCircuitContext context);

    /// <summary>记录一次尝试的结论（按 <see cref="AxiosCircuitContext.IsSuccess"/> / <see cref="AxiosCircuitContext.IsFailure"/> 判定）；两者皆否时只归还名额。放行后必然调用一次。</summary>
    void Record(AxiosCircuitContext context);
}
