using AxiosNet.Pipeline;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 端点健康记忆的状态机：冷却的进入、到期、翻倍与封顶只看单调时钟，
/// 因此这里推进注入的时钟而不是等真实时间——用 <c>Task.Delay</c> 等冷却到期时，
/// 「冷却 50ms」可能比一次失败往返还短，全量并发时用例会随机失败。
/// </summary>
public class FailoverHealthTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);

    [Fact]
    public void RecordFailure_CoolsDownOnlyAfterTheThreshold()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 2, Base, Cap);
        Assert.True(health.IsAvailable("primary"));

        health.RecordFailure("primary", failureThreshold: 2, Base, Cap);
        Assert.False(health.IsAvailable("primary"));
    }

    [Fact]
    public void IsAvailable_FlipsBackWhenTheCooldownExpires()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 1, Base, Cap);
        Assert.False(health.IsAvailable("primary"));

        // 差一个刻度仍在冷却，到点即恢复。
        now += Base.Ticks - 1;
        Assert.False(health.IsAvailable("primary"));

        now += 1;
        Assert.True(health.IsAvailable("primary"));
    }

    [Fact]
    public void RecordFailure_DoublesTheCooldownUpToTheCap()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        // 连续失败：30s / 60s / 120s / 240s，第五次 480s 被 5min 封顶。
        foreach (var seconds in new[] { 30, 60, 120, 240, 300 })
        {
            health.RecordFailure("primary", failureThreshold: 1, Base, Cap);

            now += TimeSpan.FromSeconds(seconds).Ticks - 1;
            Assert.False(health.IsAvailable("primary"));

            now += 1;
            Assert.True(health.IsAvailable("primary"));
        }
    }

    [Fact]
    public void RecordSuccess_ClearsBothFailuresAndCooldown()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 1, Base, Cap);
        health.RecordFailure("primary", failureThreshold: 1, Base, Cap);
        Assert.False(health.IsAvailable("primary"));

        health.RecordSuccess("primary");
        Assert.True(health.IsAvailable("primary"));

        // 计数也清零：再失败一次从基础冷却重新算，而不是接着上次翻倍。
        health.RecordFailure("primary", failureThreshold: 1, Base, Cap);
        now += Base.Ticks - 1;
        Assert.False(health.IsAvailable("primary"));
    }

    [Fact]
    public void RecordFailure_IsIgnoredWhenTheThresholdIsZero()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 0, Base, Cap);
        Assert.True(health.IsAvailable("primary"));
    }

    [Fact]
    public void RecordFailure_IsIgnoredWhenTheCooldownIsZero()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 1, TimeSpan.Zero, Cap);
        Assert.True(health.IsAvailable("primary"));
    }

    [Fact]
    public void Endpoints_AreTrackedIndependently()
    {
        long now = 0;
        var health = new FailoverHealth(() => now);

        health.RecordFailure("primary", failureThreshold: 1, Base, Cap);

        Assert.False(health.IsAvailable("primary"));
        Assert.True(health.IsAvailable("mirror"));
    }
}
