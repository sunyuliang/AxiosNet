namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 备用端点（<see cref="AxiosConfig.Failover"/>）：当前端点用不了时（网络错误 / 超时 / 连接池耗尽 / 熔断打开 / 5xx），
    /// 把同一份请求嫁接到下一个端点的 base URL 上重发；全部失败才交给重试。<see cref="Endpoints"/> 为空（默认）时不换端点。
    /// </summary>
    /// <remarks>
    /// 端点提供 origin 与路径前缀，请求自己的 path / query 原样接在后面；顺序即优先级。
    /// 端点的健康状态跨请求记忆（见 <see cref="FailoverSettings.FailureThreshold"/> / <see cref="FailoverSettings.Cooldown"/>）：
    /// 健康端点按原顺序排在前面，冷却中的端点退到末尾兜底 —— 全部都在冷却时仍按原顺序逐个试，不会有请求被健康表挡住。
    /// 换端点要求请求体能按顺序重放（流必须可 seek），并会丢掉调用方显式设过的 <c>Host</c> 头。
    /// 4xx、<c>ERR_QUEUE_TIMEOUT</c> / <c>ERR_QUEUE_FULL</c>、配置 / 编程错误与取消不换端点，<c>ERR_CIRCUIT_OPEN</c> 换。
    /// 流式响应只在响应头阶段能换端点。
    /// </remarks>
    public sealed class FailoverSettings
    {
        private IReadOnlyList<Uri>? _endpoints;

        /// <summary>
        /// 备用端点（有序，可带路径前缀），例如 <c>["https://backup1.example.com", "https://backup2.example.com/api"]</c>；
        /// <c>null</c> / 空集合（默认）= 不换端点。必须是绝对的 <c>http</c> / <c>https</c> 地址，且不能带 query / fragment。
        /// </summary>
        /// <remarks>
        /// 赋值时快照成数组，读回来的是这一版内部数组，<b>调用方不得修改</b>（要改请重新赋值）。
        /// 重复端点（拼出同一个 base URL，尾斜杠不算区别）在发请求前抛 <c>ERR_BAD_OPTION_VALUE</c>。
        /// </remarks>
        public IReadOnlyList<Uri>? Endpoints
        {
            get => _endpoints;
            set
            {
                _endpoints = value is null ? null : Snapshot(value);
                EndpointsSet = true;
            }
        }

        private static Uri[] Snapshot(IReadOnlyList<Uri> endpoints)
        {
            var copy = new Uri[endpoints.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = endpoints[i];
            }

            return copy;
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool EndpointsSet { get; set; }

        private int _failureThreshold = 2;

        /// <summary>
        /// 同一个端点连续失败多少次后进入冷却（默认 2，<c>0</c> 关闭健康记忆）。
        /// 冷却期内它不再作为首跳，避免主端点持续不可用时每条请求都先撞一次。
        /// </summary>
        /// <remarks>
        /// 只统计「换一个下游可能好」的故障（与 <see cref="Endpoints"/> 的换端点判据同源）：
        /// 网络错误 / 超时 / 连接池耗尽 / 熔断打开 / 5xx。4xx、取消与配置错误既不加也不减。
        /// 判定是客户端级共享状态：同一实例上的所有请求看同一份健康表，按请求改本值只影响之后记录时的阈值。
        /// </remarks>
        public int FailureThreshold
        {
            get => _failureThreshold;
            set
            {
                _failureThreshold = value;
                FailureThresholdSet = true;
            }
        }

        /// <summary>是否被显式赋值过。</summary>
        internal bool FailureThresholdSet { get; set; }

        private TimeSpan _cooldown = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 基础冷却时长（默认 30 秒，<c>TimeSpan.Zero</c> 同样关闭健康记忆）；连续失败越多冷却按 2 的幂延长，
        /// 封顶 <see cref="MaxCooldown"/>。冷却到期后端点自动重新可用，再失败立刻重新进入冷却。
        /// </summary>
        public TimeSpan Cooldown
        {
            get => _cooldown;
            set
            {
                _cooldown = value;
                CooldownSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含显式赋 <c>TimeSpan.Zero</c>）。</summary>
        internal bool CooldownSet { get; set; }

        private TimeSpan _maxCooldown = TimeSpan.FromMinutes(5);

        /// <summary>冷却上限（默认 5 分钟）；小于 <see cref="Cooldown"/> 时按 <see cref="Cooldown"/> 处理。</summary>
        public TimeSpan MaxCooldown
        {
            get => _maxCooldown;
            set
            {
                _maxCooldown = value;
                MaxCooldownSet = true;
            }
        }

        /// <summary>是否被显式赋值过。</summary>
        internal bool MaxCooldownSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的端点表覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(FailoverSettings incoming)
        {
            if (incoming.EndpointsSet)
            {
                // 两边都是不可变快照，直接共享这一版。
                _endpoints = incoming._endpoints;
                EndpointsSet = true;
            }

            if (incoming.FailureThresholdSet)
            {
                FailureThreshold = incoming.FailureThreshold;
            }

            if (incoming.CooldownSet)
            {
                Cooldown = incoming.Cooldown;
            }

            if (incoming.MaxCooldownSet)
            {
                MaxCooldown = incoming.MaxCooldown;
            }
        }

        internal FailoverSettings Clone() => (FailoverSettings)MemberwiseClone();
    }
}
