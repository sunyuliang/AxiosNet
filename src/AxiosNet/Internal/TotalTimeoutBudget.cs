using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 整条逻辑请求的总预算（<see cref="AxiosConfig.TotalTimeout"/>）：入口处把相对时长换成一次绝对时间戳，
/// 之后重试、备用端点、对冲分支共享同一个时间戳。
/// </summary>
internal static class TotalTimeoutBudget
{
    /// <summary>「没有总预算」的哨兵：真实时间戳不会是 0（进程启动后 <c>Stopwatch</c> 已走了若干 tick）。</summary>
    public const long None = 0;

    /// <summary>计时器分辨率：<see cref="Task.Delay(TimeSpan)"/> 与 <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 都截断毫秒以下的部分。</summary>
    public static readonly TimeSpan TimerResolution = TimeSpan.FromMilliseconds(1);

    /// <summary>剩余预算是否已不足以再发一次尝试：<c>null</c> = 没设总预算；不足一个计时器 tick 视为已耗尽。</summary>
    public static bool IsExhausted(TimeSpan? remaining) => remaining is { } left && left <= TimerResolution;

    /// <summary>
    /// 把配置里的相对总预算换成绝对时间戳：<paramref name="entryTimestamp"/> 是入口取的时间戳（排队与拦截器都计入）。
    /// 没设、显式 <c>null</c> 或非正数都返回 <see cref="None"/>（<c>Zero</c> 与 <c>null</c> 等价）。
    /// </summary>
    public static long Start(AxiosConfig config, long entryTimestamp)
    {
        if (config.TotalTimeout is not { } totalTimeout || totalTimeout <= TimeSpan.Zero)
        {
            return None;
        }

        // 超长预算收敛到计时器上限，避免溢出成负数。
        var budget = totalTimeout > Timeouts.Max ? Timeouts.Max : totalTimeout;
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
