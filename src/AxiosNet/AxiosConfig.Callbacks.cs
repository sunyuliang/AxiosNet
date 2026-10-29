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
        /// 拿到的是 <see cref="System.Net.Http.HttpClient"/> 交付的正文字节（已按 <c>AutomaticDecompression</c> / <c>Decompression</c> 解压，
        /// 但未做字符集解码），参数是状态码 / 响应头 / 内容类型上下文。
        /// </summary>
        /// <remarks>
        /// 触发范围：网络响应，且 <see cref="AxiosConfig.ResponseType"/> 不是 <see cref="AxiosNet.ResponseType.Stream"/> ——
        /// 流式响应没有完整字节可给；短路响应不经过网络读取路径，同样不触发。
        /// 状态码不合法时拒绝要等正文读完才抛出，因此 4xx / 5xx 也会触发（便于记录错误体）；并且每读一份正文触发一次：
        /// 被重试放弃、被对冲丢弃的分支同样会触发，对冲时多条分支会并发触发，回调需线程安全。
        /// 回调同步执行、抛异常即中断本次请求（<c>ERR_BAD_RESPONSE</c>）；
        /// <see cref="ReadOnlyMemory{T}"/> 只在回调期间有效，需要保留请自行复制。
        /// </remarks>
        public Action<AxiosResponseInfo, ReadOnlyMemory<byte>>? OnRawResponse { get; set; }

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
        }

        internal CallbackSettings Clone() => (CallbackSettings)MemberwiseClone();
    }
}
