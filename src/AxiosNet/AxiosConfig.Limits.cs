namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 体积、速率与流式超时限制（<see cref="AxiosConfig.Limits"/>）。
    /// </summary>
    public sealed class LimitSettings
    {
        private long? _maxContentLength;

        /// <summary>
        /// 对应 axios maxContentLength：响应体最大字节数，null 不限。
        /// 声明了 Content-Length 时在读取正文前拒绝，否则边读边计数，超出抛 <see cref="AxiosException"/>。
        /// 按请求显式设为 null 可清除实例默认上限。
        /// </summary>
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

        /// <summary>
        /// 对应 axios maxBodyLength：请求体最大字节数，null 不限。
        /// 仅在长度可确定时校验（未知长度的 Stream 不校验），超出抛 <see cref="AxiosException"/>。
        /// 按请求显式设为 null 可清除实例默认上限。
        /// </summary>
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

        /// <summary>
        /// 限速（字节/秒），null 不限速；同时作用于上传与下载。
        /// 按请求显式设为 null 可清除实例默认限速。
        /// <para>契约：响应流须单线程顺序读取（进度计量非线程安全）。</para>
        /// </summary>
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

        private TimeSpan? _streamIdleTimeout;

        /// <summary>
        /// 流式响应（<see cref="ResponseType.Stream"/>）正文的超时口径：
        /// <list type="bullet">
        /// <item><c>null</c>（默认）：沿用 <see cref="AxiosConfig.Timeout"/> 的「全程总预算」，正文与响应头共享同一份预算；</item>
        /// <item><c>TimeSpan.Zero</c>：正文不设超时，<see cref="AxiosConfig.Timeout"/> 只覆盖到响应头；</item>
        /// <item>大于零：正文按<b>空闲超时</b>计——只在实际读写期间计时，单次读写连续这么久没有数据才失败；
        /// 调用方处理帧的停顿不计入（错误码仍是 <c>ECONNABORTED</c>），<see cref="AxiosConfig.Timeout"/> 只覆盖到响应头。</item>
        /// </list>
        /// <para>只影响流式响应；非流式请求的正文读取始终在 <see cref="AxiosConfig.Timeout"/> 之内。负值非法。</para>
        /// </summary>
        public TimeSpan? StreamIdleTimeout
        {
            get => _streamIdleTimeout;
            set
            {
                _streamIdleTimeout = value;
                StreamIdleTimeoutSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool StreamIdleTimeoutSet { get; set; }

        private int? _maxConcurrentRequests;

        /// <summary>
        /// 在途请求并发上限（本 <see cref="AxiosClient"/> 实例范围内），<c>null</c>（默认）不限。
        /// <para>
        /// 与 <see cref="MaxRate"/> 的区别：<see cref="MaxRate"/> 限的是<b>字节速度</b>（每秒多少字节），
        /// 本项限的是<b>同时在途的请求条数</b>（同一时刻最多几个请求占着连接）。
        /// 两者正交：小请求高频调用需要的是并发上限，大文件传输需要的是速率上限。
        /// </para>
        /// <para>
        /// <b>实例级配置</b>：闸门（<see cref="SemaphoreSlim"/>）是实例共享资源，在<b>首个受限请求</b>时按本项创建并固定。
        /// 因此按请求写<b>不同</b>的值会抛 <c>ERR_BAD_OPTION_VALUE</c>（写与实例相同的值放行），
        /// 构造之后再改 <see cref="AxiosClient.Defaults"/> 同样会报错而不是静默沿用旧上限 ——
        /// 要让新上限生效请新建 <see cref="AxiosClient"/>。
        /// </para>
        /// <para>
        /// 槽位覆盖「真正在途」的那一段：排队等待不计入 <see cref="AxiosConfig.Timeout"/>（排队有
        /// <see cref="QueueTimeout"/> 单独兜底），重试的每次尝试各自重新取槽（退避等待期间不占位），
        /// 流式响应（<see cref="ResponseType.Stream"/>）则一直占到调用方释放正文流为止。
        /// </para>
        /// <para>正整数；<c>0</c> 与负数非法（抛 <c>ERR_BAD_OPTION_VALUE</c>），不限制请设为 <c>null</c>。</para>
        /// <para>
        /// <b>自锁风险</b>：闸门是实例级的，若在拦截器 / 进度回调里用<b>同一个实例</b>再发请求，
        /// 内层请求会和外层抢同一批槽位 —— 上限很小时（尤其 1）双方会一直等下去。
        /// 这类「请求里发子请求」的写法请给 <see cref="QueueTimeout"/> 设上限，或让子请求走另一个实例。
        /// </para>
        /// </summary>
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

        private TimeSpan? _queueTimeout;

        /// <summary>
        /// 等待并发槽位的上限（只在设置了 <see cref="MaxConcurrentRequests"/> 时有意义）：
        /// <list type="bullet">
        /// <item><c>null</c>（默认）：一直等，只由 <see cref="AxiosConfig.CancellationToken"/> 退出；</item>
        /// <item><see cref="TimeSpan.Zero"/>：不排队 —— 槽位占满时立刻失败，不等；</item>
        /// <item>大于零：最多等这么久，超时抛 <c>ERR_QUEUE_TIMEOUT</c>（<b>不是</b> <c>ECONNABORTED</c>，
        /// 队列超时不是请求超时，两者的处置方式不同）。</item>
        /// </list>
        /// <para>按请求项：同一次调用里写不同的值合法（它只影响本次等待，不改变闸门本身）。负值非法。</para>
        /// </summary>
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

        /// <summary>
        /// 流式单帧上限的下界（1 MB）：比正常帧还小的上限没有实际意义，也容易和「不限制」混淆。
        /// </summary>
        public const int MinStreamFrameLength = 1024 * 1024;

        /// <summary>
        /// 流式单帧上限的上界（64 MB）。两个原因：分帧的字节累计（<c>_elementSpilled + length</c>）与缓冲倍增
        /// （<c>_buffer.Length * 2</c>）都是 int 运算，再往上会溢出成负数绕过判断；且峰值内存是上限的 2–4 倍
        /// （读缓冲 + 溢写缓冲 + 解码后的 UTF-16 字符串），64 MB 已对应约 256 MB 峰值。
        /// </summary>
        public const int MaxStreamFrameLengthLimit = 64 * 1024 * 1024;

        private int? _maxStreamFrameLength;

        /// <summary>
        /// 流式分帧的单帧上限（字节）：NDJSON 一行 / SSE 一帧 / JSON 数组的一个顶层元素。
        /// <para>
        /// <c>null</c>（默认）等于 <see cref="DefaultStreamFrameLength"/>（8 MB）。
        /// 与 <see cref="MaxContentLength"/> 的 <c>null = 不限</c> 不同：分帧器必须把整帧读进内存才能交给
        /// 序列化器，「不限」等于把内存上限交给服务端决定，所以这里没有关闭语义 —— 只能收紧或放大。
        /// </para>
        /// <para>
        /// 合法区间 <see cref="MinStreamFrameLength"/>–<see cref="MaxStreamFrameLengthLimit"/>（1–64 MB），
        /// 越界抛 <c>ERR_BAD_OPTION_VALUE</c>（在请求发出前拦下）。
        /// 峰值内存约为上限的 2–4 倍：读缓冲 + 溢写缓冲 + 解码后的字符串（UTF-16），
        /// SSE 的帧累积缓冲同样按 2 倍字符计 —— 所以放大之前先确认这份预算可以接受。
        /// </para>
        /// <para>
        /// 只影响声明式客户端的 <c>RequestStreamAsync</c>（核心客户端的 <see cref="ResponseType.Stream"/>
        /// 交出裸流、不做分帧，因此没有单帧概念，受 <see cref="MaxContentLength"/> 约束）。
        /// 设置方式与其它限制项一致：实例默认（<c>new ApiClient(baseUrl, d => d.Limits.MaxStreamFrameLength = ...)</c>）、
        /// 按请求 <c>configure</c>，或直接改 <see cref="AxiosConfig.Limits"/>。
        /// </para>
        /// <para>
        /// 边界：上限是「帧内容的字节数」，NDJSON 还要多留一个换行符的位置，所以恰好等于上限的一行会先撞上
        /// 缓冲上限（差 1 字节，实际使用不必贴着边界算）。
        /// </para>
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

        /// <summary>
        /// 把 <paramref name="incoming"/> 里显式设置过的限制项覆盖到本实例（合并层内部使用）。
        /// <para>这几项都允许显式设为 <c>null</c>：体积与速率类是「不限制」，分帧上限是「回到默认 8 MB」——
        /// 两者都是有效意图，所以必须看 *Set 标记而不是看值。</para>
        /// </summary>
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

            if (incoming.StreamIdleTimeoutSet)
            {
                StreamIdleTimeout = incoming.StreamIdleTimeout;
            }

            // 并发上限虽属实例级（不同值会被校验层拦下），仍要合并进来：
            // 否则「按请求写了别的值」根本到不了校验层，会被静默丢弃。
            if (incoming.MaxConcurrentRequestsSet)
            {
                MaxConcurrentRequests = incoming.MaxConcurrentRequests;
            }

            if (incoming.QueueTimeoutSet)
            {
                QueueTimeout = incoming.QueueTimeout;
            }

            if (incoming.MaxStreamFrameLengthSet)
            {
                // 显式 null 是有效意图（回到默认 8 MB），因此必须看标记而不是看值
                MaxStreamFrameLength = incoming.MaxStreamFrameLength;
            }
        }

        internal LimitSettings Clone() => (LimitSettings)MemberwiseClone();
    }
}
