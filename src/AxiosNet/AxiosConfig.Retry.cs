using AxiosNet.Retry;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>弹性重试（<see cref="AxiosConfig.Retry"/>）：不设置 <see cref="Policy"/> 时不重试。</summary>
    public sealed class RetrySettings
    {
        private IAxiosRetryPolicy? _policy;

        /// <summary>重试策略，<c>null</c> 表示不重试（默认）；显式赋 <c>null</c> 是本次请求关掉实例默认的重试。</summary>
        public IAxiosRetryPolicy? Policy
        {
            get => _policy;
            set
            {
                _policy = value;
                PolicySet = true;
            }
        }

        /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool PolicySet { get; set; }

        /// <summary>每次真正发起重试前的回调（对应 axios-retry 的 <c>onRetry</c>），在请求线程上同步执行，抛异常会中断本次请求。</summary>
        /// <remarks>入参里的 <see cref="AxiosRetryContext.Delay"/> 已是本次等待时间；单次请求上设置的回调替换实例默认回调，<c>null</c> 不表示清空。</remarks>
        public Action<AxiosRetryContext>? OnRetry { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的重试设置覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(RetrySettings incoming)
        {
            if (incoming.PolicySet)
            {
                Policy = incoming.Policy;
            }

            OnRetry = incoming.OnRetry ?? OnRetry;
        }

        internal RetrySettings Clone() => (RetrySettings)MemberwiseClone();
    }
}
