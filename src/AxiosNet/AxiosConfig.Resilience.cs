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

    private bool HasResilienceValues
        => _retry is not null
            || _circuit is not null
            || _hedging is not null
            || _failover is not null
            || _idempotency is not null
            || _allowUnsafeRetrySet
            || _totalTimeoutSet
            || (_limits is { MaxConcurrentRequestsSet: true } or { MaxQueueLengthSet: true } or { QueueTimeoutSet: true });

    private bool _allowUnsafeRetry;

    /// <summary>重试与对冲是否允许作用在非幂等方法上（唯一开关）。</summary>
    internal bool AllowUnsafeRetry => _allowUnsafeRetry;

    private bool _allowUnsafeRetrySet;

    /// <summary>是否被显式赋值过（含显式 false），用于合并配置时区分「未设置」与「显式关闭」。</summary>
    internal bool AllowUnsafeRetrySet => _allowUnsafeRetrySet;

    /// <summary>并发闸门（原 <see cref="Limits"/> 的三项）：闸门是实例级状态，变更请新建客户端。</summary>
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

        /// <summary>是否被显式赋值过（含显式 false），合并层用。</summary>
        internal bool AllowUnsafeRetryIsSet => _owner._allowUnsafeRetrySet;

        /// <inheritdoc cref="AxiosConfig.TotalTimeoutSet"/>
        internal bool TotalTimeoutIsSet => _owner._totalTimeoutSet;

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

            // 并发项仍由 Limits 合并块处理（实例级项不搬运的规则在那里），这里不重复搬。

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

    /// <summary>并发闸门（<see cref="ResilienceSettings.Concurrency"/>）：底层即 <see cref="LimitSettings"/> 的三项并发设置。</summary>
    public sealed class ConcurrencySettings
    {
        private readonly AxiosConfig _owner;

        internal ConcurrencySettings(AxiosConfig owner) => _owner = owner;

        /// <summary>实例内在途请求上限，<c>null</c>（默认）不限；闸门在首个受限请求时创建并固定。</summary>
        public int? MaxInFlight
        {
            get => _owner.Limits.MaxConcurrentRequests;
            set => _owner.Limits.MaxConcurrentRequests = value;
        }

        /// <summary>等待槽位的请求条数上限，只在设置 <see cref="MaxInFlight"/> 时有意义。</summary>
        public int? MaxQueue
        {
            get => _owner.Limits.MaxQueueLength;
            set => _owner.Limits.MaxQueueLength = value;
        }

        /// <summary>等待并发槽位的上限，只在设置 <see cref="MaxInFlight"/> 时有意义。</summary>
        public TimeSpan? QueueTimeout
        {
            get => _owner.Limits.QueueTimeout;
            set => _owner.Limits.QueueTimeout = value;
        }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的并发项覆盖到本实例。</summary>
        internal void MergeFrom(LimitSettings incoming) => _owner.Limits.MergeFrom(incoming);
    }
}
