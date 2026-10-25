using AxiosNet.Retry;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 弹性重试（<see cref="AxiosConfig.Retry"/>）。
    /// <para>
    /// <b>默认不重试</b>：<see cref="Policy"/> 为 <c>null</c>（未设置）时，请求管线完全不走重试分支，
    /// 与没有这个功能时的行为、分配完全一致。需要重试时挂一个策略：
    /// 用内置的 <see cref="Retry.RetryPolicy"/>（可配次数与间隔），或自己实现 <see cref="IAxiosRetryPolicy"/>。
    /// </para>
    /// </summary>
    public sealed class RetrySettings
    {
        private IAxiosRetryPolicy? _policy;

        /// <summary>
        /// 重试策略；<c>null</c> 表示<b>不重试</b>（默认）。
        /// <para>
        /// 典型用法：<c>config.Retry.Policy = new RetryPolicy { MaxRetries = 3, Delay = TimeSpan.FromMilliseconds(200) }</c>。
        /// </para>
        /// <para>
        /// 显式赋 <c>null</c> 是「本次请求关掉实例默认的重试」，与「没设置」在合并层是两件事：
        /// 前者会压住实例默认策略，后者沿用实例默认策略。
        /// </para>
        /// </summary>
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

        /// <summary>
        /// 每次真正发起重试前的回调（对应 axios-retry 的 <c>onRetry</c>）：可用于日志、埋点或指标。
        /// <para>
        /// 入参里的 <see cref="AxiosRetryContext.Delay"/> 已经是本次等待时间。
        /// 回调在请求线程上同步执行，抛出的异常会中断本次请求并原样抛给调用方
        /// （不会被当成「本次尝试失败」再重试一次）。
        /// </para>
        /// <para>
        /// 单次请求上设置的回调<b>替换</b>实例默认回调；不设置则沿用实例默认。
        /// 想在某次请求上「不记日志」请用 <see cref="Policy"/> 关掉重试，
        /// 或让回调自己判断——这里没有「显式设为 null 以清空实例默认」的语义（与 <see cref="Policy"/> 不同）。
        /// </para>
        /// </summary>
        public Action<AxiosRetryContext>? OnRetry { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里的重试设置覆盖到本实例（合并层内部使用）。
        /// <para>
        /// <see cref="Policy"/> 允许显式设为 <c>null</c> 来关闭实例默认的重试，因此必须看
        /// <see cref="PolicySet"/> 标记；<see cref="OnRetry"/> 是纯回调，<c>null</c> 即「未设置」。
        /// </para>
        /// </summary>
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
