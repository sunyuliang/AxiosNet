namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>进度与原始字节回调、状态校验与超时文案（<see cref="AxiosConfig.Callbacks"/>）。</summary>
    public sealed class CallbackSettings
    {
        /// <summary>上传进度回调（对应 axios onUploadProgress），写入请求体时约 50ms 触发一次；在请求线程上同步执行，抛异常会中断请求。</summary>
        public Action<AxiosProgressEvent>? OnUploadProgress { get; set; }

        /// <summary>下载进度回调（对应 axios onDownloadProgress），读取响应体时触发；响应流须单线程顺序读取（进度计量非线程安全）。</summary>
        public Action<AxiosProgressEvent>? OnDownloadProgress { get; set; }

        /// <summary>
        /// 原始响应字节回调：正文读完后、文本解码与反序列化（以及 <see cref="AxiosConfig.TransformResponse"/>）之前触发，
        /// 参数是状态码 / 响应头 / 内容类型上下文与已解压的原始字节。
        /// </summary>
        /// <remarks>
        /// 只对非流式网络响应触发，每次尝试触发一次（含被重试放弃、被对冲丢弃的分支）；对冲时多条分支并发触发，回调需线程安全。
        /// 同步执行，抛异常即中断本次请求（<c>ERR_BAD_RESPONSE</c>）；<see cref="ReadOnlyMemory{T}"/> 只在回调期间有效。
        /// </remarks>
        public Action<AxiosResponseInfo, ReadOnlyMemory<byte>>? OnRawResponse { get; set; }

        /// <summary>
        /// 发送前钩子：请求消息成型（URL 已拼好、正文已按 <see cref="Data"/> 序列化、请求头已写入）之后、真正发出之前触发，
        /// 参数是本次尝试的请求消息、生效配置与本次尝试的取消令牌。
        /// </summary>
        /// <remarks>
        /// 每次尝试触发一次：重试 / 换端点 / 对冲的每条分支各一次，因此可以在这里写只对某次发送有效的头（签名、时间戳）。
        /// 必须在多次尝试间保持不变的键（幂等键）不要写在这里——每次尝试都会重新生成；请用 <see cref="AxiosConfig.Idempotency"/>。
        /// 异步执行，抛异常即中断本次请求（<c>ERR_BAD_REQUEST</c>，原始异常在 <see cref="Exception.InnerException"/>）；
        /// 读 <see cref="HttpRequestMessage.Content"/> 时注意：已缓冲的正文直接取字节，流式正文会被读走，包了进度 / 限速的内容会再序列化一遍。
        /// </remarks>
        public Func<HttpRequestMessage, AxiosConfig, CancellationToken, ValueTask>? OnSendingRequest { get; set; }

        /// <summary>自定义超时错误消息（对应 axios timeoutMessage），为空时使用默认文案。</summary>
        public string? TimeoutMessage { get; set; }

        /// <summary>对应 axios validateStatus，返回 false 时抛出异常；null 表示默认（2xx 合法）。</summary>
        public Func<int, bool>? ValidateStatus { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里有值的项覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(CallbackSettings incoming)
        {
            ValidateStatus = incoming.ValidateStatus ?? ValidateStatus;
            TimeoutMessage = incoming.TimeoutMessage ?? TimeoutMessage;
            OnUploadProgress = incoming.OnUploadProgress ?? OnUploadProgress;
            OnDownloadProgress = incoming.OnDownloadProgress ?? OnDownloadProgress;
            OnRawResponse = incoming.OnRawResponse ?? OnRawResponse;
            OnSendingRequest = incoming.OnSendingRequest ?? OnSendingRequest;
        }

        internal CallbackSettings Clone() => (CallbackSettings)MemberwiseClone();
    }
}
