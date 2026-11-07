namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>可观测性（<see cref="AxiosConfig.Observability"/>）：采集档位与进度 / 原始字节 / 发送前回调、超时文案。</summary>
    public sealed class ObservabilitySettings
    {
        private TelemetryCapture? _capture;
        private Action<AxiosProgressEvent>? _onUploadProgress;
        private Action<AxiosProgressEvent>? _onDownloadProgress;
        private Action<AxiosResponseInfo, ReadOnlyMemory<byte>>? _onRawResponse;
        private Func<HttpRequestMessage, AxiosConfig, CancellationToken, ValueTask>? _onSendingRequest;
        private string? _timeoutMessage;

        /// <summary>
        /// 本次请求的遥测采集档位，<c>null</c>（默认）= <see cref="TelemetryCapture.Path"/>；
        /// <see cref="TelemetryCapture.Off"/> 时不建 Activity、不写指标、不发事件。
        /// </summary>
        /// <remarks>按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。</remarks>
        public TelemetryCapture? Capture
        {
            get => _capture;
            set
            {
                _capture = value;
                CaptureSet = true;
            }
        }

        /// <summary>上传进度回调（对应 axios onUploadProgress），写入请求体时约 50ms 触发一次；在请求线程上同步执行，抛异常会中断请求。</summary>
        /// <remarks>按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。</remarks>
        public Action<AxiosProgressEvent>? OnUploadProgress
        {
            get => _onUploadProgress;
            set
            {
                _onUploadProgress = value;
                OnUploadProgressSet = true;
            }
        }

        /// <summary>下载进度回调（对应 axios onDownloadProgress），读取响应体时触发；响应流须单线程顺序读取（进度计量非线程安全）。</summary>
        /// <remarks>按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。</remarks>
        public Action<AxiosProgressEvent>? OnDownloadProgress
        {
            get => _onDownloadProgress;
            set
            {
                _onDownloadProgress = value;
                OnDownloadProgressSet = true;
            }
        }

        /// <summary>
        /// 原始响应字节回调：正文读完后、文本解码与反序列化（以及 <see cref="SerializationSettings.TransformResponse"/>）之前触发，
        /// 参数是状态码 / 响应头 / 内容类型上下文与已解压的原始字节。
        /// </summary>
        /// <remarks>
        /// 只对非流式网络响应触发，每次尝试触发一次（含被重试放弃、被对冲丢弃的分支）；对冲时多条分支并发触发，回调需线程安全。
        /// 同步执行，抛异常即中断本次请求（<c>ERR_BAD_RESPONSE</c>）；<see cref="ReadOnlyMemory{T}"/> 只在回调期间有效。
        /// 按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。
        /// </remarks>
        public Action<AxiosResponseInfo, ReadOnlyMemory<byte>>? OnRawResponse
        {
            get => _onRawResponse;
            set
            {
                _onRawResponse = value;
                OnRawResponseSet = true;
            }
        }

        /// <summary>
        /// 发送前钩子：请求消息成型（URL 已拼好、正文已按 <see cref="Body"/> 序列化、请求头已写入）之后、真正发出之前触发，
        /// 参数是本次尝试的请求消息、生效配置与本次尝试的取消令牌。
        /// </summary>
        /// <remarks>
        /// 每次尝试触发一次：重试 / 换端点 / 对冲的每条分支各一次，因此可以在这里写只对某次发送有效的头（签名、时间戳）。
        /// 必须在多次尝试间保持不变的键（幂等键）不要写在这里——每次尝试都会重新生成；请用 <see cref="ResilienceSettings.Idempotency"/>。
        /// 异步执行，抛异常即中断本次请求（<c>ERR_BAD_REQUEST</c>，原始异常在 <see cref="Exception.InnerException"/>）；
        /// 读 <see cref="HttpRequestMessage.Content"/> 时注意：已缓冲的正文直接取字节，流式正文会被读走，包了进度 / 限速的内容会再序列化一遍。
        /// 按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。
        /// </remarks>
        public Func<HttpRequestMessage, AxiosConfig, CancellationToken, ValueTask>? OnSendingRequest
        {
            get => _onSendingRequest;
            set
            {
                _onSendingRequest = value;
                OnSendingRequestSet = true;
            }
        }

        /// <summary>自定义超时错误消息（对应 axios timeoutMessage），为空时使用默认文案。</summary>
        /// <remarks>按请求显式写 <c>null</c> 会清除实例默认值；省略则继承。</remarks>
        public string? TimeoutMessage
        {
            get => _timeoutMessage;
            set
            {
                _timeoutMessage = value;
                TimeoutMessageSet = true;
            }
        }

        internal bool CaptureSet { get; private set; }

        internal bool OnUploadProgressSet { get; private set; }

        internal bool OnDownloadProgressSet { get; private set; }

        internal bool OnRawResponseSet { get; private set; }

        internal bool OnSendingRequestSet { get; private set; }

        internal bool TimeoutMessageSet { get; private set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里显式赋值过的项覆盖到本实例（合并层内部使用）。
        /// 用 <c>*Set</c> 而不是「非 null」判定：按请求写 <c>null</c> 表示清除实例默认值，省略才继承。
        /// </summary>
        internal void MergeFrom(ObservabilitySettings incoming)
        {
            if (incoming.CaptureSet)
            {
                _capture = incoming._capture;
                CaptureSet = true;
            }

            if (incoming.TimeoutMessageSet)
            {
                _timeoutMessage = incoming._timeoutMessage;
                TimeoutMessageSet = true;
            }

            if (incoming.OnUploadProgressSet)
            {
                _onUploadProgress = incoming._onUploadProgress;
                OnUploadProgressSet = true;
            }

            if (incoming.OnDownloadProgressSet)
            {
                _onDownloadProgress = incoming._onDownloadProgress;
                OnDownloadProgressSet = true;
            }

            if (incoming.OnRawResponseSet)
            {
                _onRawResponse = incoming._onRawResponse;
                OnRawResponseSet = true;
            }

            if (incoming.OnSendingRequestSet)
            {
                _onSendingRequest = incoming._onSendingRequest;
                OnSendingRequestSet = true;
            }
        }

        /// <summary>
        /// 清掉「显式赋值过」标记，值保持不变（合并层建新配置时用）。
        /// 从实例默认值克隆出来的标记只说明「有值」，不说明「调用方本次写过」：清掉之后，
        /// 回放 <see cref="AxiosResponse.Config"/> 时才不会把来源实例的默认值 / 清空意图搬到目标实例上；
        /// 调用方显式写入的叶子会在 <see cref="MergeFrom"/> 里重新置标记。
        /// </summary>
        internal void ClearSetFlags()
        {
            CaptureSet = false;
            OnUploadProgressSet = false;
            OnDownloadProgressSet = false;
            OnRawResponseSet = false;
            OnSendingRequestSet = false;
            TimeoutMessageSet = false;
        }

        /// <summary>浅拷贝（合并层用）：回调委托与字符串按引用共享；<c>*Set</c> 标记随副本一起保留。</summary>
        internal ObservabilitySettings Clone() => (ObservabilitySettings)MemberwiseClone();
    }
}

/// <summary>遥测采集档位（<see cref="AxiosConfig.ObservabilitySettings.Capture"/>）。</summary>
public enum TelemetryCapture
{
    /// <summary>不产出遥测：不建 Activity、不写指标、不发事件。</summary>
    Off = 0,

    /// <summary>产出遥测，<c>url.full</c> 只到 path（不含查询串）。默认档。</summary>
    Path = 1,

    /// <summary>产出遥测，<c>url.full</c> 含查询串。</summary>
    Query = 2,

    /// <summary>产出遥测，但不写 URL 标签。</summary>
    NoUrl = 3,
}
