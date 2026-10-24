namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 进度回调、状态校验与超时文案（<see cref="AxiosConfig.Callbacks"/>）。
    /// </summary>
    public sealed class CallbackSettings
    {
        /// <summary>
        /// 上传进度回调（对应 axios onUploadProgress）。写入请求体时触发，约 50ms 一次；
        /// 回调在请求线程上同步执行，抛异常会中断请求。
        /// <para>
        /// 重定向（307/308）或重试重发正文时，<see cref="AxiosProgressEvent.Loaded"/> <b>跨次累计</b>
        /// （进度不回跳，百分比封顶 100%）。
        /// </para>
        /// </summary>
        public Action<AxiosProgressEvent>? OnUploadProgress { get; set; }

        /// <summary>
        /// 下载进度回调（对应 axios onDownloadProgress）。读取响应体时触发；
        /// 流式响应（<see cref="ResponseType.Stream"/>）在调用方读取流的过程中触发。
        /// <para>契约：同一请求的响应流须单线程顺序读取（进度计量非线程安全）。</para>
        /// </summary>
        public Action<AxiosProgressEvent>? OnDownloadProgress { get; set; }

        /// <summary>自定义超时错误消息（对应 axios timeoutMessage），为空时使用默认文案。</summary>
        public string? TimeoutMessage { get; set; }

        /// <summary>对应 axios validateStatus，返回 false 时抛出异常；null 表示默认（2xx 合法）。</summary>
        public Func<int, bool>? ValidateStatus { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里「有值」的项覆盖到本实例（合并层内部使用）。
        /// <para>本组项都是引用类型，<c>null</c> 即「未设置」，因此直接 <c>??</c> 回退。</para>
        /// </summary>
        internal void MergeFrom(CallbackSettings incoming)
        {
            ValidateStatus = incoming.ValidateStatus ?? ValidateStatus;
            TimeoutMessage = incoming.TimeoutMessage ?? TimeoutMessage;
            OnUploadProgress = incoming.OnUploadProgress ?? OnUploadProgress;
            OnDownloadProgress = incoming.OnDownloadProgress ?? OnDownloadProgress;
        }

        internal CallbackSettings Clone() => (CallbackSettings)MemberwiseClone();
    }
}
