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

        /// <summary>
        /// 把 <paramref name="incoming"/> 里显式设置过的限制项覆盖到本实例（合并层内部使用）。
        /// <para>这几项都允许显式设为 <c>null</c> 来清除实例默认上限，因此必须看 *Set 标记。</para>
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
        }

        internal LimitSettings Clone() => (LimitSettings)MemberwiseClone();
    }
}
