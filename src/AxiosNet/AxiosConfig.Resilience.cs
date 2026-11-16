using System;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    // Resilience 是既有五个治理设置的统一入口：子对象仍是原来的类型与实例，
    // 这里只做分组收敛，不拍平、不复制状态。
    private ResilienceSettings? _resilienceFacade;

    /// <summary>
    /// 流量治理的统一入口：重试 / 熔断 / 并发对冲 / 备用端点 / 并发闸门。
    /// 五个子对象原样保留，配置写法从「五个顶层分组」收敛为「一个分组 + 五个子对象」。
    /// </summary>
    /// <remarks>
    /// <see cref="AllowUnsafeRetry"/> 是重试与对冲共用的唯一「非幂等方法放行」开关；
    /// <see cref="ResilienceSettings.TotalTimeout"/> 是跨尝试的总预算，与单次尝试的 <see cref="Timeout"/> 区分。
    /// </remarks>
    public ResilienceSettings Resilience => _resilienceFacade ??= new ResilienceSettings(this);

    /// <summary>本次配置是否真的碰过治理项（只读 facade 不算），供合并层判断「这一组有没有按请求设置」。</summary>
    internal ResilienceSettings? ResilienceOrNull
        => HasResilienceValues ? Resilience : null;

    /// <summary>等槽位上限（不分配门面）：闸门中间件每次尝试都要取它。</summary>
    internal TimeSpan? ConcurrencyQueueTimeout => _concurrencyState?.QueueTimeout;

    private bool HasResilienceValues => _retry is not null
            || _circuit is not null
            || _hedging is not null
            || _failover is not null
            || _idempotency is not null
            || _allowUnsafeRetrySet
            || _totalTimeoutSet
            || _concurrencyState is { QueueTimeoutSet: true };

    private bool _allowUnsafeRetry;

    /// <summary>重试与对冲是否允许作用在非幂等方法上（唯一开关）。</summary>
    internal bool AllowUnsafeRetry => _allowUnsafeRetry;

    private bool _allowUnsafeRetrySet;

    // 排队超时的存储：只在真正碰过这一项时才分配。在途上限与排队深度属实例级选项
    // （AxiosClientOptions），闸门容量在构造期定死，不存在「按请求改容量」这条路径。
    private ConcurrencyState? _concurrencyState;

    private sealed class ConcurrencyState
    {
        /// <summary>等槽位上限的按请求覆盖值；<c>null</c> 表示继承实例默认（见 <see cref="ConcurrencyQueueTimeout"/>）。</summary>
        internal TimeSpan? QueueTimeout;

        /// <summary><see cref="QueueTimeout"/> 是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool QueueTimeoutSet;

        /// <summary>按指针浅拷贝：两个字段都可独立改写，克隆出的状态不共享。</summary>
        internal ConcurrencyState Clone() => (ConcurrencyState)MemberwiseClone();
    }

    /// <summary>
    /// <see cref="ResilienceSettings.Concurrency"/> 的门面缓存：首次访问时创建，之后复用，
    /// 避免热路径上反复 new。门面只转发到本实例、本身无状态，故可长期共享。
    /// </summary>
    private ConcurrencySettings? _concurrencyFacade;

    /// <summary>治理分组：重试 / 熔断 / 对冲 / 备用端点 / 并发闸门 / 幂等键。</summary>
    public sealed class ResilienceSettings
    {
        private readonly AxiosConfig _owner;

        internal ResilienceSettings(AxiosConfig owner) => _owner = owner;

        /// <summary>重试：不设置 <see cref="RetrySettings.Policy"/> 即不重试。</summary>
        public RetrySettings Retry
        {
            get => _owner._retry ??= new RetrySettings();
            set => _owner._retry = value;
        }

        /// <summary>熔断：不设置 <see cref="CircuitSettings.Policy"/> 即不熔断。</summary>
        public CircuitSettings Circuit
        {
            get => _owner._circuit ??= new CircuitSettings();
            set => _owner._circuit = value;
        }

        /// <summary>并发对冲：<see cref="HedgingSettings.MaxHedges"/> 为 0 即不对冲。</summary>
        public HedgingSettings Hedging
        {
            get => _owner._hedging ??= new HedgingSettings();
            set => _owner._hedging = value;
        }

        /// <summary>备用端点：<see cref="FailoverSettings.Endpoints"/> 为空即不换端点。</summary>
        public FailoverSettings Failover
        {
            get => _owner._failover ??= new FailoverSettings();
            set => _owner._failover = value;
        }

        /// <summary>并发闸门：在途上限、排队上限与排队超时。</summary>
        public ConcurrencySettings Concurrency
            => _owner._concurrencyFacade ??= new ConcurrencySettings(_owner);

        /// <summary>幂等键：打开后每个逻辑请求写一个键，重试 / 换端点 / 对冲共用同一个键（默认关闭）。</summary>
        public IdempotencySettings Idempotency
        {
            get => _owner._idempotency ??= new IdempotencySettings();
            set => _owner._idempotency = value;
        }

        /// <summary>
        /// 整条逻辑请求的总预算，跨重试 / 备用端点 / 对冲共享；<c>null</c> = 不设。
        /// 单次尝试的上限仍是 <see cref="AxiosConfig.Timeout"/>。
        /// </summary>
        public TimeSpan? TotalTimeout
        {
            get => _owner._totalTimeout;
            set
            {
                _owner._totalTimeout = value;
                _owner._totalTimeoutSet = true;
            }
        }

        /// <summary>
        /// 是否允许重试 / 对冲作用于 POST / PATCH 等非幂等方法，默认 <c>false</c>。
        /// 打开前请确认接口幂等：重发可能让服务端收到两次写操作。
        /// </summary>
        public bool AllowUnsafeRetry
        {
            get => _owner._allowUnsafeRetry;
            set
            {
                _owner._allowUnsafeRetry = value;
                _owner._allowUnsafeRetrySet = true;
            }
        }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的治理项覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(ResilienceSettings incoming)
        {
            var source = incoming._owner;

            if (source._retry is not null)
            {
                Retry.MergeFrom(source._retry);
            }

            if (source._circuit is not null)
            {
                Circuit.MergeFrom(source._circuit);
            }

            if (source._hedging is not null)
            {
                Hedging.MergeFrom(source._hedging);
            }

            if (source._failover is not null)
            {
                Failover.MergeFrom(source._failover);
            }

            if (source._idempotency is not null)
            {
                Idempotency.MergeFrom(source._idempotency);
            }

            // 排队等待上限按请求项：闸门中间件每次尝试都读它。
            if (source._concurrencyState is { QueueTimeoutSet: true } incomingQueueTimeout)
            {
                Concurrency.QueueTimeout = incomingQueueTimeout.QueueTimeout;
            }

            if (source._totalTimeoutSet)
            {
                TotalTimeout = source._totalTimeout;
            }

            if (source._allowUnsafeRetrySet)
            {
                AllowUnsafeRetry = source._allowUnsafeRetry;
            }
        }
    }

    /// <summary>并发闸门的排队等待上限（<see cref="ResilienceSettings.Concurrency"/>）：在途上限与排队深度属实例级选项，不在配置上。</summary>
    public sealed class ConcurrencySettings
    {
        private readonly AxiosConfig _owner;

        internal ConcurrencySettings(AxiosConfig owner) => _owner = owner;

        /// <summary>
        /// 等待并发槽位的上限，只在 <see cref="AxiosClientOptions.MaxInFlight"/> 有值时才有意义；
        /// <c>null</c>（默认）= 一直等，<see cref="TimeSpan.Zero"/> = 不排队（容量满即刻失败）。
        /// </summary>
        /// <remarks>按请求项：单次请求可以改本次的等待上限；容量本身改不了（那是实例级的）。</remarks>
        public TimeSpan? QueueTimeout
        {
            get => _owner._concurrencyState?.QueueTimeout;
            set
            {
                var state = _owner._concurrencyState ??= new ConcurrencyState();
                state.QueueTimeout = value;
                state.QueueTimeoutSet = true;
            }
        }
    }
}
