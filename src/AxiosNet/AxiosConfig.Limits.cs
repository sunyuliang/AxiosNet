namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>体积、速率与流式超时限制（<see cref="AxiosConfig.Limits"/>）。</summary>
    public sealed class LimitSettings
    {
        private long? _maxContentLength;

        /// <summary>
        /// 对应 axios maxContentLength：响应体最大字节数，<c>null</c>（默认）不限 —— 与 axios 的 <c>-1</c> 同口径，
        /// 库不会为「没设上限」发出任何告警或日志；超出抛 <see cref="AxiosException"/>，按请求显式设为 null 可清除实例默认上限。
        /// </summary>
        /// <remarks>缓冲型响应（非 <see cref="ResponseType.Stream"/>）会把整个正文读进内存：对不可信或体量未知的下游，生产环境请显式设上限。</remarks>
        public long? MaxContentLength
        {
            get => _maxContentLength;
            set
            {
                _maxContentLength = value;
                MaxContentLengthSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxContentLengthSet { get; set; }

        private long? _maxBodyLength;

        /// <summary>对应 axios maxBodyLength：请求体最大字节数，null 不限；仅在长度可确定时校验，超出抛 <see cref="AxiosException"/>。</summary>
        public long? MaxBodyLength
        {
            get => _maxBodyLength;
            set
            {
                _maxBodyLength = value;
                MaxBodyLengthSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxBodyLengthSet { get; set; }

        private long? _maxRate;

        /// <summary>限速（字节/秒），null 不限速，同时作用于上传与下载；响应流须单线程顺序读取。</summary>
        public long? MaxRate
        {
            get => _maxRate;
            set
            {
                _maxRate = value;
                MaxRateSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxRateSet { get; set; }

        private int? _maxConcurrentRequests;

        /// <summary>
        /// 在途请求并发上限（本 <see cref="AxiosClient"/> 实例范围内），<c>null</c>（默认）不限；正整数，<c>0</c> 与负数抛 <c>ERR_BAD_OPTION_VALUE</c>。
        /// 实例级配置：闸门在首个受限请求时创建并固定，按请求写不同的值抛错；排队等待不计入
        /// <see cref="AxiosConfig.Timeout"/>，但计入 <see cref="AxiosConfig.Deadline"/>（见 <see cref="QueueTimeout"/>）。
        /// </summary>
        /// <remarks>闸门是实例级的：在拦截器 / 回调里用同一实例发子请求会抢同一批槽位，请给 <see cref="QueueTimeout"/> 设上限或换实例。</remarks>
        public int? MaxConcurrentRequests
        {
            get => _maxConcurrentRequests;
            set
            {
                _maxConcurrentRequests = value;
                MaxConcurrentRequestsSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxConcurrentRequestsSet { get; set; }

        private int? _maxQueueLength;

        /// <summary>
        /// 排队等待槽位的请求条数上限（只在设置了 <see cref="MaxConcurrentRequests"/> 时有意义）：<c>null</c>（默认）不限；
        /// 正整数；队列已满时新来的请求立刻失败（<c>ERR_QUEUE_FULL</c>），不进入等待。
        /// </summary>
        /// <remarks>
        /// 计数对象是「正在等槽位的尝试」而不是逻辑请求：重试 / 备用端点 / 对冲的每次尝试各排一次队。
        /// 与 <see cref="MaxConcurrentRequests"/> 一样是实例级配置：在首个受限请求时固定，按请求写不同的值抛
        /// <c>ERR_BAD_OPTION_VALUE</c>。被拒的请求不进排队水位（它没有真的等过），但要计入闸门拒绝指标。
        /// </remarks>
        public int? MaxQueueLength
        {
            get => _maxQueueLength;
            set
            {
                _maxQueueLength = value;
                MaxQueueLengthSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxQueueLengthSet { get; set; }

        private TimeSpan? _queueTimeout;

        /// <summary>
        /// 等待并发槽位的上限（只在设置了 <see cref="MaxConcurrentRequests"/> 时有意义）：<c>null</c>（默认）一直等；
        /// <see cref="TimeSpan.Zero"/> 不排队，槽位占满时立刻失败；大于零超时抛 <c>ERR_QUEUE_TIMEOUT</c>。负值非法。
        /// </summary>
        /// <remarks>
        /// 本项只管「排队能等多久」，不改变排队时间记在哪个预算上：排队计入
        /// <see cref="AxiosConfig.Deadline"/>（不计入 <see cref="AxiosConfig.Timeout"/>）。因此设了总预算时
        /// 实际等待 = <c>min(本项, Deadline 剩余)</c> —— 被总预算截断时报 <c>ECONNABORTED</c> 而不是
        /// <c>ERR_QUEUE_TIMEOUT</c>，两者指向不同的旋钮。<c>null</c>（一直等）同样受这条约束。
        /// </remarks>
        public TimeSpan? QueueTimeout
        {
            get => _queueTimeout;
            set
            {
                _queueTimeout = value;
                QueueTimeoutSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool QueueTimeoutSet { get; set; }

        /// <summary>流式单帧上限的默认值（8 MB），与「设了 <c>null</c>」等价。</summary>
        public const int DefaultStreamFrameLength = 8 * 1024 * 1024;

        /// <summary>流式单帧上限的下界（1 MB）。</summary>
        public const int MinStreamFrameLength = 1024 * 1024;

        /// <summary>流式单帧上限的上界（64 MB）：再往上分帧的 int 运算会溢出，峰值内存也会过大。</summary>
        public const int MaxStreamFrameLengthLimit = 64 * 1024 * 1024;

        private int? _maxStreamFrameLength;

        /// <summary>
        /// 流式分帧的单帧上限（字节）：NDJSON 一行 / SSE 一帧 / JSON 数组的一个顶层元素。
        /// <c>null</c>（默认）等于 <see cref="DefaultStreamFrameLength"/>（8 MB），合法区间 1–64 MB，越界抛 <c>ERR_BAD_OPTION_VALUE</c>。
        /// 只影响声明式客户端的 <c>RequestStreamAsync</c>。
        /// </summary>
        public int? MaxStreamFrameLength
        {
            get => _maxStreamFrameLength;
            set
            {
                _maxStreamFrameLength = value;
                MaxStreamFrameLengthSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxStreamFrameLengthSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的限制项覆盖到本实例（合并层内部使用）；各项都允许显式设为 <c>null</c>，因此看 *Set 标记而不是看值。</summary>
        internal void MergeFrom(LimitSettings incoming)
        {
            if (incoming.MaxContentLengthSet)
            {
                MaxContentLength = incoming.MaxContentLength;
            }

            if (incoming.MaxBodyLengthSet)
            {
                MaxBodyLength = incoming.MaxBodyLength;
            }

            if (incoming.MaxRateSet)
            {
                MaxRate = incoming.MaxRate;
            }

            // 并发上限虽属实例级，仍要合并进来，否则「按请求写了别的值」到不了校验层。
            if (incoming.MaxConcurrentRequestsSet)
            {
                MaxConcurrentRequests = incoming.MaxConcurrentRequests;
            }

            if (incoming.MaxQueueLengthSet)
            {
                MaxQueueLength = incoming.MaxQueueLength;
            }

            if (incoming.QueueTimeoutSet)
            {
                QueueTimeout = incoming.QueueTimeout;
            }

            if (incoming.MaxStreamFrameLengthSet)
            {
                // 显式 null 是有效意图（回到默认 8 MB），必须看标记而不是看值。
                MaxStreamFrameLength = incoming.MaxStreamFrameLength;
            }
        }

        internal LimitSettings Clone() => (LimitSettings)MemberwiseClone();
    }
}
