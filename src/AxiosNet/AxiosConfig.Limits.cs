namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 体积与流式分帧上限（真分组，也是这三项的唯一入口）：请求体上限 / 响应体上限 / 流式单帧上限。
    /// 每个叶子只有一份存储、一份注释、一个入口；首次读取时懒创建。
    /// </summary>
    public sealed class LimitsSettings
    {
        private long? _maxResponseBytes;

        /// <summary>
        /// 响应体最大字节数：<c>null</c>（默认）不限，与 axios 的 <c>maxContentLength = -1</c> 同口径；
        /// 超出抛 <see cref="AxiosException"/>，按请求显式设为 <c>null</c> 可清除实例默认上限。
        /// 普通响应边读边计数，<c>ResponseType.Stream</c> 先按声明长度预检再累计。
        /// </summary>
        public long? MaxResponseBytes
        {
            get => _maxResponseBytes;
            set
            {
                _maxResponseBytes = value;
                MaxResponseBytesSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxResponseBytesSet { get; set; }

        private long? _maxRequestBytes;

        /// <summary>
        /// 请求体最大字节数：<c>null</c>（默认）不限；超出抛 <see cref="AxiosException"/>。
        /// 只对能算出长度的正文生效（JSON / 表单 / <c>byte[]</c> / 可 seek 流）；
        /// 不可 seek 流、<c>IAsyncEnumerable&lt;byte[]&gt;</c>、multipart 等未知长度正文不拦截，需要硬上限请在调用方自行限制。
        /// 开启 <see cref="AxiosConfig.Compression"/> 时按压缩后（实际发送）的字节数校验。
        /// </summary>
        public long? MaxRequestBytes
        {
            get => _maxRequestBytes;
            set
            {
                _maxRequestBytes = value;
                MaxRequestBytesSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxRequestBytesSet { get; set; }

        private int? _maxStreamFrameBytes;

        /// <summary>
        /// 流式分帧的单帧上限（字节）：NDJSON 一行 / SSE 一帧 / JSON 数组的一个顶层元素。
        /// <c>null</c>（默认）等于 <see cref="AxiosConfig.DefaultStreamFrameBytes"/>（8 MB），
        /// 合法区间 <see cref="AxiosConfig.MinStreamFrameBytes"/>–<see cref="AxiosConfig.MaxStreamFrameBytesLimit"/>，越界抛 <c>ERR_BAD_OPTION_VALUE</c>。
        /// 只影响流式响应的分帧读取。
        /// </summary>
        public int? MaxStreamFrameBytes
        {
            get => _maxStreamFrameBytes;
            set
            {
                _maxStreamFrameBytes = value;
                MaxStreamFrameBytesSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool MaxStreamFrameBytesSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的限制项覆盖到本实例（合并层内部使用）；各项都允许显式设为 <c>null</c>，因此看 *Set 标记而不是看值。</summary>
        internal void MergeFrom(LimitsSettings incoming)
        {
            if (incoming.MaxResponseBytesSet)
            {
                MaxResponseBytes = incoming.MaxResponseBytes;
            }

            if (incoming.MaxRequestBytesSet)
            {
                MaxRequestBytes = incoming.MaxRequestBytes;
            }

            if (incoming.MaxStreamFrameBytesSet)
            {
                // 显式 null 回到默认 8 MB，看标记而不是看值。
                MaxStreamFrameBytes = incoming.MaxStreamFrameBytes;
            }
        }

        /// <summary>浅拷贝（合并层用）：只含值类型，直接成员拷贝。</summary>
        internal LimitsSettings Clone() => (LimitsSettings)MemberwiseClone();
    }
}
