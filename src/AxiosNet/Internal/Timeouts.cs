namespace AxiosNet.Internal;

/// <summary>
/// 超时值的收敛规则。
/// <para>
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 底层是计时器队列，
/// 延迟上限为 <c>uint.MaxValue - 1</c> 毫秒（约 49.7 天）；超出会抛裸
/// <see cref="ArgumentOutOfRangeException"/>，既不是 <see cref="AxiosException"/>，
/// 也会在请求体已经创建之后打断调用链（调用方拿到的是一句「参数 delay 越界」）。
/// </para>
/// <para>
/// 因此这里统一收敛：超过上限按上限处理（语义上仍是「极长但有限的超时」），
/// 调用方不需要知道计时器的实现细节。要真正的无限超时用 <see cref="AxiosConfig.Timeout"/> = 0。
/// </para>
/// </summary>
internal static class Timeouts
{
    /// <summary>计时器可接受的最大延迟（比 <c>uint.MaxValue-1</c> 再留 1 毫秒余量）。</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 2);

    /// <summary>把超时值收敛到计时器可接受的范围内。</summary>
    public static TimeSpan Clamp(TimeSpan value) => value > Max ? Max : value;
}
