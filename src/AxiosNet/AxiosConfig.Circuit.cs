using AxiosNet.Circuit;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 熔断（<see cref="AxiosConfig.Circuit"/>）：下游持续故障时<b>连请求都不发</b>，
    /// 直接以 <c>ERR_CIRCUIT_OPEN</c> 失败，给下游留出恢复窗口。
    /// <para>
    /// <b>默认不熔断</b>：<see cref="Policy"/> 为 <c>null</c>（未设置）时，请求管线完全不走熔断分支，
    /// 与没有这个功能时的行为、分配一致。需要熔断时挂一个策略：用内置的
    /// <see cref="Circuit.CircuitBreaker"/>（按 host 计数 + 半开探测 + 自动恢复），或自己实现
    /// <see cref="IAxiosCircuitPolicy"/>。
    /// </para>
    /// <para>
    /// 与 <see cref="RetrySettings"/> 是两件事、可以同时开：重试解决「这一次失败了要不要再试」，
    /// 熔断解决「下游整体不可用时还要不要试」。熔断闸门在<b>每次尝试之前</b>，
    /// 所以熔断一旦打开，重试循环里的下一次尝试会被立刻拦下（不再浪费退避等待）。
    /// </para>
    /// </summary>
    public sealed class CircuitSettings
    {
        private IAxiosCircuitPolicy? _policy;

        /// <summary>
        /// 熔断策略；<c>null</c> 表示<b>不熔断</b>（默认）。
        /// <para>
        /// 典型用法：<c>config.Circuit.Policy = new CircuitBreaker { FailureThreshold = 5, BreakDuration = TimeSpan.FromSeconds(30) }</c>。
        /// </para>
        /// <para>
        /// 策略实例会被<b>并发请求共享</b>，且熔断状态就存在策略里：想让多个客户端共享同一份熔断状态就传同一个实例，
        /// 想各算各的就各自 new 一个。显式赋 <c>null</c> 是「本次请求关掉实例默认的熔断」
        /// （健康检查、探针这类请求不该被熔断挡住），与「没设置」在合并层是两件事。
        /// </para>
        /// </summary>
        public IAxiosCircuitPolicy? Policy
        {
            get => _policy;
            set
            {
                _policy = value;
                PolicySet = true;
            }
        }

        /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool PolicySet { get; set; }

        /// <summary>
        /// 请求<b>被熔断拒绝</b>时的回调（请求没发出去，只发生在拒绝这一刻）。
        /// <para>
        /// 入参是本次尝试的上下文：<see cref="AxiosCircuitContext.Host"/> 是计数维度，
        /// <see cref="AxiosCircuitContext.RetryAfter"/> 是策略给出的建议等待 ——
        /// 写日志 / 埋点不必为了拿等待时间再去 catch 异常。
        /// 状态迁移（打开 / 恢复）另由 <see cref="CircuitBreaker.StateChanged"/> 通知 ——
        /// 那是策略级事件，不随单次请求变化。
        /// </para>
        /// <para>
        /// 回调在请求线程上同步执行，抛出的异常会中断本次请求并原样抛给调用方
        /// （不会变成 <c>ERR_CIRCUIT_OPEN</c>）。单次请求上设置的回调<b>替换</b>实例默认回调。
        /// </para>
        /// </summary>
        public Action<AxiosCircuitContext>? OnRejected { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里的熔断设置覆盖到本实例（合并层内部使用）。
        /// <para>
        /// <see cref="Policy"/> 允许显式设为 <c>null</c> 来关闭实例默认的熔断，因此必须看
        /// <see cref="PolicySet"/> 标记；<see cref="OnRejected"/> 是纯回调，<c>null</c> 即「未设置」。
        /// </para>
        /// </summary>
        internal void MergeFrom(CircuitSettings incoming)
        {
            if (incoming.PolicySet)
            {
                Policy = incoming.Policy;
            }

            OnRejected = incoming.OnRejected ?? OnRejected;
        }

        internal CircuitSettings Clone() => (CircuitSettings)MemberwiseClone();
    }
}
