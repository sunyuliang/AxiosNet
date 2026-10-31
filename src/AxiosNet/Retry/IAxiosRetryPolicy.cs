namespace AxiosNet.Retry;

/// <summary>
/// 重试策略：决定一次失败后要不要再试一次，以及等多久再试。
/// </summary>
/// <remarks>
/// 默认不重试（<see cref="AxiosConfig.RetrySettings.Policy"/> 为 <c>null</c> 时管线不进重试分支）。
/// 实现会被同一实例的并发请求共享，必须线程安全。
/// 次数到顶、调用方已取消、错误本身是取消、熔断拒绝、并发排队超时 / 队列满、
/// 尝试阶段的配置错误（<c>ERR_BAD_OPTION_VALUE</c>）这六类情况库已提前挡掉，策略改不掉。
/// </remarks>
/// <example>
/// <code>
/// // 默认实现：最多重试 3 次，间隔 200ms 起、每次翻倍，带抖动
/// client.Defaults.Retry.Policy = new RetryPolicy { MaxRetries = 3 };
///
/// // 只重试「业务码 5001」：自定义策略
/// public sealed class BizRetryPolicy : IAxiosRetryPolicy
/// {
///     public int MaxRetries => 2;
///
///     public bool ShouldRetry(AxiosRetryContext context)
///         => context.Response?.GetHeader("X-Biz-Code") == "5001";
///
///     public TimeSpan GetDelay(AxiosRetryContext context) => TimeSpan.FromMilliseconds(300);
/// }
/// </code>
/// </example>
public interface IAxiosRetryPolicy
{
    /// <summary>最大重试次数（不含首次请求）：<c>0</c> 表示不重试；负值非法，抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    int MaxRetries { get; }

    /// <summary>是否重试本次失败；返回 <c>false</c> 时本次失败原样抛给调用方（异常不包装、不改错误码）。</summary>
    bool ShouldRetry(AxiosRetryContext context);

    /// <summary>本次重试前的等待时间；<see cref="TimeSpan.Zero"/> 表示立刻重试（负值按不等待处理）。</summary>
    TimeSpan GetDelay(AxiosRetryContext context);
}
