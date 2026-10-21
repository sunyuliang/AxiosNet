namespace AxiosNet.Internal;

/// <summary>
/// 超时值的收敛规则。
/// <para>
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 底层是计时器队列，延迟上限为
/// <c>uint.MaxValue - 1</c> 毫秒（约 49.7 天），超出会抛裸 <see cref="ArgumentOutOfRangeException"/>。
/// 这里统一收敛到上限（语义仍是「极长但有限」）；要真正的无限超时用 <see cref="AxiosConfig.Timeout"/> = 0。
/// </para>
/// </summary>
internal static class Timeouts
{
    /// <summary>计时器可接受的最大延迟（比 <c>uint.MaxValue-1</c> 再留 1 毫秒余量）。</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 2);

    /// <summary>把超时值收敛到计时器可接受的范围内。</summary>
    public static TimeSpan Clamp(TimeSpan value) => value > Max ? Max : value;
}
