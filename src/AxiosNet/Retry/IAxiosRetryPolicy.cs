namespace AxiosNet.Retry;

/// <summary>
/// 重试策略：决定一次失败后「要不要再试一次」以及「等多久再试」。
/// <para>
/// <b>默认不重试</b>：<see cref="AxiosConfig.RetrySettings.Policy"/> 为 <c>null</c> 时管线完全不进重试分支。
/// 需要一个开箱可用的实现时用 <see cref="RetryPolicy"/>（可配次数、间隔、退避、抖动、状态码），
/// 需要完全自定义（例如「按业务响应码重试」或「按截止时间重试」）时实现本接口。
/// </para>
/// <para>
/// 实现应保持<b>无状态且线程安全</b>：同一个策略实例通常挂在实例默认值上，被并发请求共享。
/// 库在调用前已经挡掉了三类不该重试的情况（次数到顶、调用方已取消、错误本身是取消），
/// 策略里不必再判断这些。
/// </para>
/// </summary>
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
    /// <summary>
    /// 最大重试次数（<b>不含</b>首次请求）：<c>0</c> 表示不重试，<c>3</c> 表示最多再发 3 次。
    /// <para>
    /// 这是循环的硬上限，<see cref="ShouldRetry"/> 只能在此基础上提前收敛（例如「只对 5xx 重试」）。
    /// 负值非法，抛 <c>ERR_BAD_OPTION_VALUE</c>。想「一直重试到取消」请给一个足够大的值，
    /// 并用 <see cref="AxiosConfig.CancellationToken"/> 控制退出。
    /// </para>
    /// </summary>
    int MaxRetries { get; }

    /// <summary>
    /// 是否重试本次失败。<paramref name="context"/> 里带着尝试序号、异常与响应。
    /// <para>返回 <c>false</c> 时，本次失败原样抛给调用方（异常不包装、不改错误码）。</para>
    /// </summary>
    bool ShouldRetry(AxiosRetryContext context);

    /// <summary>
    /// 本次重试前的等待时间；返回 <see cref="TimeSpan.Zero"/> 表示立刻重试。
    /// <para>
    /// 只在 <see cref="ShouldRetry"/> 返回 <c>true</c> 之后调用。返回值会被收敛到 <c>&gt;= 0</c>：
    /// 返回负值等价于不等待（不抛异常，避免「策略算错时间」把整个请求变成配置错误）。
    /// </para>
    /// </summary>
    TimeSpan GetDelay(AxiosRetryContext context);
}
