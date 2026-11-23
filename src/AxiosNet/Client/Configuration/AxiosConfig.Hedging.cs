namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>并发对冲（<see cref="AxiosConfig.ResilienceSettings.Hedging"/>）：按 <see cref="Delay"/> 的间隔追加同样的请求，第一个成功结果胜出，其余分支取消。</summary>
    /// <remarks>
    /// <see cref="MaxHedges"/> 为 <c>0</c>（默认）时不对冲；所有分支都失败时把失败交给重试。流式响应、未显式允许的非幂等方法、
    /// 正文不能并发重放、响应侧带共享状态的请求一律按单次发送处理。
    /// </remarks>
    public sealed class HedgingSettings
    {
        /// <summary>对冲分支数的上限。</summary>
        internal const int MaxHedgesLimit = 16;

        private int _maxHedges;

        /// <summary>额外的并发分支数，<c>0</c>（默认）表示不对冲；每条分支占一个槽位，<see cref="AxiosClientOptions.MaxInFlight"/> 需按 <c>1 + MaxHedges</c> 倍预留。</summary>
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

        /// <summary>第 n 条对冲分支的起跑延迟 = <c>n × Delay</c>（第 0 条立发），默认 80ms；<c>TimeSpan.Zero</c> 表示所有分支同时起跑。</summary>
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
        }

        /// <summary>浅拷贝（合并层用）：策略与委托共用，不深克隆。</summary>
        internal HedgingSettings Clone() => (HedgingSettings)MemberwiseClone();
    }
}
