using System.Diagnostics;
using AxiosNet.Internal;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 总预算的换算与耗尽判定（<see cref="TotalTimeoutBudget"/>）：这里直接断言「不足一个计时器 tick 即耗尽」这条边界。
/// <para>
/// 这条边界只在计时器截断时才出现：<c>CancelAfter</c> / <c>Task.Delay</c> 都按整毫秒计时，被总预算压小的尝试可能
/// 早于截止点不到 1ms 就报超时，此时剩余预算是正的零点几毫秒。余量为负还是正取决于定时器是否过冲，本机与 CI
/// 会落在不同侧，所以结论不能交给端到端用例去碰运气。
/// </para>
/// </summary>
public sealed class TotalTimeoutBudgetTests
{
    [Fact]
    public void IsExhausted_TreatsSubMillisecondRemainderAsSpent()
    {
        // 定时器把 0.4ms 压成 0ms：下一次尝试发不出去，判据必须为真。
        Assert.True(TotalTimeoutBudget.IsExhausted(TimeSpan.FromMilliseconds(0.4)));
        Assert.True(TotalTimeoutBudget.IsExhausted(TimeSpan.FromTicks(1)));
        Assert.True(TotalTimeoutBudget.IsExhausted(TotalTimeoutBudget.TimerResolution));
        Assert.True(TotalTimeoutBudget.IsExhausted(TimeSpan.Zero));
        Assert.True(TotalTimeoutBudget.IsExhausted(TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void IsExhausted_KeepsBudgetThatOutlivesATimerTick()
    {
        Assert.False(TotalTimeoutBudget.IsExhausted(TimeSpan.FromMilliseconds(2)));
        Assert.False(TotalTimeoutBudget.IsExhausted(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IsExhausted_NoBudgetIsNeverExhausted()
        => Assert.False(TotalTimeoutBudget.IsExhausted(null));

    [Fact]
    public void Start_NoBudgetMeansNone()
    {
        var now = Stopwatch.GetTimestamp();

        Assert.Equal(TotalTimeoutBudget.None, TotalTimeoutBudget.Start(new AxiosConfig(), now));
        Assert.Equal(TotalTimeoutBudget.None, TotalTimeoutBudget.Start(new AxiosConfig { TotalTimeout = TimeSpan.Zero }, now));
        Assert.Null(TotalTimeoutBudget.Remaining(TotalTimeoutBudget.None));
    }

    [Fact]
    public void Start_ConvertsRelativeBudgetToAbsoluteDeadline()
    {
        var deadline = TotalTimeoutBudget.Start(
            new AxiosConfig { TotalTimeout = TimeSpan.FromMilliseconds(150) },
            Stopwatch.GetTimestamp());

        var remaining = TotalTimeoutBudget.Remaining(deadline);

        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.FromMilliseconds(140), TimeSpan.FromMilliseconds(151));
    }

    [Fact]
    public void Start_ClampsBudgetThatWouldOverflow()
    {
        // 超长预算换算成 tick 会溢出成负数（那会变成「立刻耗尽」）：必须收敛到计时器上限。
        var deadline = TotalTimeoutBudget.Start(
            new AxiosConfig { TotalTimeout = TimeSpan.MaxValue },
            Stopwatch.GetTimestamp());

        Assert.True(TotalTimeoutBudget.Remaining(deadline) > TimeSpan.FromDays(1));
    }
}
