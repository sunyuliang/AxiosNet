using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 整条逻辑请求的总预算（<see cref="AxiosConfig.Deadline"/>）：入口处把相对时长换成<b>一次</b>绝对时间戳，
/// 之后重试、备用端点、对冲分支共享同一个时间戳 —— 晚起跑的分支自然拿到更少的剩余，不需要各自计时。
/// </summary>
/// <remarks>
/// 用 <see cref="Stopwatch.GetTimestamp"/> 而不是 <see cref="DateTimeOffset"/>：单调时钟不受系统时间调整影响，
/// 与熔断、遥测的计时口径一致。时间戳 <see cref="None"/>（0）表示「没有总预算」，省掉一层 nullable 判断。
/// </remarks>
internal static class DeadlineBudget
{
    /// <summary>「没有总预算」的哨兵：任何真实时间戳都不会是 0（进程启动后 <c>Stopwatch</c> 已走了若干 tick）。</summary>
    public const long None = 0;

    /// <summary>
    /// 把配置里的相对总预算换成绝对时间戳：<paramref name="entryTimestamp"/> 是调用入口取的时间戳（排队与拦截器都计入）。
    /// 没设、显式 <c>null</c> 或非正数都返回 <see cref="None"/>（<c>Zero</c> 与 <c>null</c> 等价，与 <c>Timeout</c> 一致）。
    /// </summary>
    public static long Start(AxiosConfig config, long entryTimestamp)
    {
        if (config.Deadline is not { } deadline || deadline <= TimeSpan.Zero)
        {
            return None;
        }

        // 与 Timeout 一样收敛到计时器可表示的范围：超长预算等价于「不设」，不该溢出成负数。
        var budget = deadline > Timeouts.Max ? Timeouts.Max : deadline;
        return entryTimestamp + (long)(budget.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>距总预算耗尽还剩多久；<c>null</c> = 没设总预算，<see cref="TimeSpan.Zero"/> = 已耗尽（含过期后的负值）。</summary>
    public static TimeSpan? Remaining(long timestamp)
    {
        if (timestamp == None)
        {
            return null;
        }

        var seconds = (double)(timestamp - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }
}
