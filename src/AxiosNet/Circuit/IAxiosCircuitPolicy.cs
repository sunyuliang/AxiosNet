namespace AxiosNet.Circuit;

/// <summary>
/// 熔断策略：决定一次尝试「发不发得出去」（<see cref="ShouldAllow"/>），以及把这次尝试的结果
/// 记到哪个维度的账上（<see cref="Record"/>）。
/// <para>
/// <b>默认不熔断</b>：<see cref="AxiosConfig.CircuitSettings.Policy"/> 为 <c>null</c> 时管线完全不走熔断分支，
/// 与没有这个功能时的行为、分配一致。需要熔断时挂一个策略：用内置的 <see cref="CircuitBreaker"/>
/// （按 host 计数 + 半开探测 + 自动恢复），或自己实现本接口。
/// </para>
/// <para>
/// 与 <see cref="Retry.IAxiosRetryPolicy"/> 的分工：重试解决「这一次失败了，要不要再试」，
/// 熔断解决「下游整体不可用时，要不要连试都不试」。两者可以同时开：熔断闸门在<b>每次尝试之前</b>，
/// 所以熔断打开后，重试循环里的下一次尝试会被立刻拦下（不再浪费退避等待）。
/// </para>
/// <para>
/// 实现应保持<b>线程安全</b>：同一个策略实例通常挂在实例默认值上，被并发请求共享；
/// 与重试策略不同，熔断策略<b>天然有状态</b>（按维度累计失败数、记录冷却起点），
/// 状态维护必须自己保证并发正确（<see cref="CircuitBreaker"/> 用「每维度一把锁」实现）。
/// </para>
/// <para>
/// <see cref="ShouldAllow"/> / <see cref="GetRetryAfter"/> / <see cref="Record"/> 抛出的异常会
/// <b>原样抛给调用方</b>，在失败路径上还会<b>掩盖本次尝试的原始失败</b>。策略里请只做状态维护，
/// 需要副作用（日志 / 埋点）请用 <see cref="AxiosConfig.CircuitSettings.OnRejected"/> 与
/// <see cref="CircuitBreaker.StateChanged"/>。
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 内置实现：5 次连续失败后熔断 30 秒，冷却结束放 1 个探测请求
/// config.Circuit.Policy = new CircuitBreaker
/// {
///     FailureThreshold = 5,
///     BreakDuration = TimeSpan.FromSeconds(30)
/// };
///
/// // 自定义策略：只熔断「连续 3 次 5xx」，且冷却期固定 10 秒
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
    /// 请求发出前的准入判定：返回 <c>false</c> 时本次尝试<b>不会发出</b>，
    /// 直接以 <c>ERR_CIRCUIT_OPEN</c> 失败（<see cref="AxiosException.RetryAfter"/> 带建议等待时间）。
    /// <para>
    /// 每次尝试前都会调用一次（含重试的每一次）。半开态的「探测名额」通常在这里领取：
    /// 放行即占一个名额，随后必须有一次 <see cref="Record"/> 归还。
    /// </para>
    /// <para>
    /// 实现不应有阻塞式等待：这里只做判断，等多久由调用方决定（拒绝时直接失败，不排队）。
    /// </para>
    /// </summary>
    bool ShouldAllow(AxiosCircuitContext context);

    /// <summary>
    /// 被拒绝时「还要等多久」（仅用于错误消息与 <see cref="AxiosException.RetryAfter"/>）；
    /// 返回 <see cref="TimeSpan.Zero"/> 表示说不准（例如半开态的探测名额随时可能释放）。
    /// <para>只在 <see cref="ShouldAllow"/> 返回 <c>false</c> 之后调用。</para>
    /// </summary>
    TimeSpan GetRetryAfter(AxiosCircuitContext context);

    /// <summary>
    /// 记录一次尝试的结论：<paramref name="context"/>.<see cref="AxiosCircuitContext.Exception"/> 为
    /// <c>null</c> 表示成功，否则按 <see cref="AxiosCircuitContext.IsFailure"/> 判断算不算下游故障。
    /// <para>
    /// 放行之后<b>必然</b>会调用一次（成功、失败、取消 / 4xx 都会），即使请求被取消也会走到这里 ——
    /// 半开态的探测名额靠它归还，实现不要漏掉「既非成功也非故障」这一支（应保持状态不变）。
    /// </para>
    /// </summary>
    void Record(AxiosCircuitContext context);
}
