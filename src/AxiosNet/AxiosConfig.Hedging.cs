namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 并发对冲（<see cref="AxiosConfig.Hedging"/>）：首次发送之后，按 <see cref="Delay"/> 的间隔再放出一条（或几条）同样的请求，
    /// <b>第一个成功结果胜出</b>，其余分支取消并收尾；<b>所有分支都失败</b>时把失败交给重试。
    /// <see cref="MaxHedges"/> 为 <c>0</c>（默认）时不对冲。
    /// </summary>
    /// <remarks>流式响应、非幂等方法（未显式允许时）、正文不能并发重放、响应侧带共享状态的请求一律按单次发送处理。</remarks>
    public sealed class HedgingSettings
    {
        /// <summary>对冲分支数的上限。</summary>
        internal const int MaxHedgesLimit = 16;

        private int _maxHedges;

        /// <summary>
        /// 额外的并发分支数，<c>0</c>（默认）表示不对冲；<c>1</c> 表示首发 + 一条对冲分支，依此类推。
        /// 每条分支各占一个并发槽位（熔断只记一笔），<see cref="LimitSettings.MaxConcurrentRequests"/> 要按 <c>1 + MaxHedges</c> 倍预留。
        /// </summary>
        public int MaxHedges
        {
            get => _maxHedges;
            set
            {
                _maxHedges = value;
                MaxHedgesSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool MaxHedgesSet { get; set; }

        private TimeSpan _delay = TimeSpan.FromMilliseconds(80);

        /// <summary>
        /// 第 n 条对冲分支的起跑延迟 = <c>n × Delay</c>（第 0 条立发），默认 80ms。
        /// <c>TimeSpan.Zero</c> 表示所有分支同时起跑（同一时刻并发）。
        /// </summary>
        public TimeSpan Delay
        {
            get => _delay;
            set
            {
                _delay = value;
                DelaySet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool DelaySet { get; set; }

        private bool _hedgeNonIdempotentMethods;

        /// <summary>
        /// 是否对 POST / PATCH 等非幂等方法也对冲，默认 <c>false</c>：重发可能让服务端收到两次写操作，
        /// 打开前请确认接口幂等。
        /// </summary>
        public bool HedgeNonIdempotentMethods
        {
            get => _hedgeNonIdempotentMethods;
            set
            {
                _hedgeNonIdempotentMethods = value;
                HedgeNonIdempotentMethodsSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool HedgeNonIdempotentMethodsSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的项覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(HedgingSettings incoming)
        {
            if (incoming.MaxHedgesSet)
            {
                MaxHedges = incoming.MaxHedges;
            }

            if (incoming.DelaySet)
            {
                Delay = incoming.Delay;
            }

            if (incoming.HedgeNonIdempotentMethodsSet)
            {
                HedgeNonIdempotentMethods = incoming.HedgeNonIdempotentMethods;
            }
        }

        internal HedgingSettings Clone() => (HedgingSettings)MemberwiseClone();
    }
}
