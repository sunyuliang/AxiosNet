namespace AxiosNet.Internal;

/// <summary>
/// 超时值的收敛规则：<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 的延迟上限约 49.7 天，
/// 超出会抛 <see cref="ArgumentOutOfRangeException"/>，这里统一收敛到上限（无限超时用 <see cref="AxiosConfig.Timeout"/> = 0）。
/// </summary>
internal static class Timeouts
{
    /// <summary>计时器可接受的最大延迟（比 <c>uint.MaxValue-1</c> 再留 1 毫秒余量）。</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 2);

    /// <summary>把超时值收敛到计时器可接受的范围内。</summary>
    public static TimeSpan Clamp(TimeSpan value) => value > Max ? Max : value;
}
