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
        private bool _captureRawResponseBody;

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
        /// 原始响应字节回调：正文读完后、文本解码与反序列化之前触发，参数是状态码 / 响应头 / 内容类型上下文与已解压的原始字节。
        /// </summary>
        /// <remarks>
        /// 只对非流式网络响应触发，每次尝试触发一次；对冲时多条分支并发触发，回调需线程安全。
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
        /// 发送前钩子：请求消息成型之后、真正发出之前触发，参数是本次尝试的请求消息、生效配置与本次尝试的取消令牌。
        /// </summary>
        /// <remarks>
        /// 每次尝试触发一次：重试 / 换端点 / 对冲的每条分支各一次，可以在这里写只对某次发送有效的头（签名、时间戳）。
        /// 必须在多次尝试间保持不变的键不要写在这里，请用 <see cref="ResilienceSettings.Idempotency"/>。
        /// 异步执行，抛异常即中断本次请求（<c>ERR_BAD_REQUEST</c>）；读 <see cref="HttpRequestMessage.Content"/> 时流式正文会被读走。
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

        /// <summary>
        /// 成功响应是否把正文原始字节留在 <see cref="AxiosResponse.RawBody"/> 上（默认 <c>false</c>）：请求返回后仍可读，供记日志与排查。
        /// </summary>
        /// <remarks>
        /// 打开后整段正文跟着响应对象一起存活；错误响应、短路 / 缓存命中与 <c>byte[]</c> 形态的正文本来就有，不受本项影响。
        /// 网络流式响应没有完整字节。与 <see cref="OnRawResponse"/> 的差别是「随响应交付」还是「回调里借用一次」，两者字节一致。
        /// 按请求显式写 <c>false</c> 会关掉实例默认值；省略则继承。
        /// </remarks>
        public bool CaptureRawResponseBody
        {
            get => _captureRawResponseBody;
            set
            {
                _captureRawResponseBody = value;
                CaptureRawResponseBodySet = true;
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

        /// <summary><see cref="Capture"/> 是否被显式赋值过（含显式 <c>null</c>），合并层据此区分「未设置」与「设为空」。</summary>
        internal bool CaptureSet { get; private set; }

        /// <summary><see cref="OnUploadProgress"/> 是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool OnUploadProgressSet { get; private set; }

        /// <summary><see cref="OnDownloadProgress"/> 是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool OnDownloadProgressSet { get; private set; }

        /// <summary><see cref="OnRawResponse"/> 是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool OnRawResponseSet { get; private set; }

        /// <summary><see cref="OnSendingRequest"/> 是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool OnSendingRequestSet { get; private set; }

        /// <summary><see cref="TimeoutMessage"/> 是否被显式赋值过（含显式 <c>null</c>，表示清除实例默认文案），合并层用。</summary>
        internal bool TimeoutMessageSet { get; private set; }

        /// <summary><see cref="CaptureRawResponseBody"/> 是否被显式赋值过（含显式 <c>false</c>，表示关掉实例默认值），合并层用。</summary>
        internal bool CaptureRawResponseBodySet { get; private set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里显式赋值过的项覆盖到本实例；按 <c>*Set</c> 判定，写 <c>null</c> 表示清除实例默认值。
        /// </summary>
        internal void MergeFrom(ObservabilitySettings incoming)
        {
            // 查标记 → 抄值 → 抄标记。
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

            if (incoming.CaptureRawResponseBodySet)
            {
                _captureRawResponseBody = incoming._captureRawResponseBody;
                CaptureRawResponseBodySet = true;
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
        /// 清掉「显式赋值过」标记，值保持不变；调用方显式写入的叶子会在 <see cref="MergeFrom"/> 里重新置标记。
        /// </summary>
        internal void ClearSetFlags()
        {
            CaptureSet = false;
            OnUploadProgressSet = false;
            OnDownloadProgressSet = false;
            OnRawResponseSet = false;
            OnSendingRequestSet = false;
            TimeoutMessageSet = false;
            CaptureRawResponseBodySet = false;
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
