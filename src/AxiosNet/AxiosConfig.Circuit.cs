using AxiosNet.Circuit;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 熔断（<see cref="AxiosConfig.Circuit"/>）：下游持续故障时连请求都不发，直接以 <c>ERR_CIRCUIT_OPEN</c> 失败。
    /// 不设置 <see cref="Policy"/> 时不熔断；熔断闸门在每次尝试之前，重试循环的下一次尝试会被立刻拦下。
    /// </summary>
    public sealed class CircuitSettings
    {
        private IAxiosCircuitPolicy? _policy;

        /// <summary>熔断策略，<c>null</c> 表示不熔断（默认）；显式赋 <c>null</c> 是本次请求关掉实例默认的熔断。</summary>
        /// <remarks>策略实例会被并发请求共享，熔断状态就存在策略里：想共享状态就传同一个实例。</remarks>
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

        /// <summary>请求被熔断拒绝时的回调（请求没发出去，只发生在拒绝这一刻）；在请求线程上同步执行，抛异常会中断本次请求。</summary>
        /// <remarks>入参是本次尝试的上下文：<see cref="AxiosCircuitContext.Host"/> 是计数维度，<see cref="AxiosCircuitContext.RetryAfter"/> 是策略给出的建议等待时间。</remarks>
        public Action<AxiosCircuitContext>? OnRejected { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的熔断设置覆盖到本实例（合并层内部使用）。</summary>
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
