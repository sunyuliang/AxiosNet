using AxiosNet.Circuit;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 熔断状态迁移载荷（<see cref="AxiosCircuitTransition"/>）：告警分级直接读这几个布尔，
/// 空迁移用 <see cref="AxiosCircuitTransition.IsEmpty"/> 区分。
/// </summary>
public sealed class CircuitTransitionTests
{
    [Fact]
    public void Transition_ReportsCurrentState()
    {
        var opened = new AxiosCircuitTransition("api:443", AxiosCircuitState.Closed, AxiosCircuitState.Open);

        Assert.Equal("api:443", opened.Host);
        Assert.Equal(AxiosCircuitState.Closed, opened.Previous);
        Assert.Equal(AxiosCircuitState.Open, opened.Current);
        Assert.True(opened.IsOpen);
        Assert.False(opened.IsClosed);
        Assert.False(opened.IsHalfOpen);
        Assert.False(opened.IsEmpty);
        Assert.Equal("api:443: Closed -> Open", opened.ToString());
    }

    [Fact]
    public void Transition_ProbeAndRecoveryFlags()
    {
        var probing = new AxiosCircuitTransition("api", AxiosCircuitState.Open, AxiosCircuitState.HalfOpen);
        Assert.True(probing.IsHalfOpen);
        Assert.False(probing.IsOpen);
        Assert.False(probing.IsClosed);

        var recovered = new AxiosCircuitTransition("api", AxiosCircuitState.HalfOpen, AxiosCircuitState.Closed);
        Assert.True(recovered.IsClosed);
        Assert.False(recovered.IsOpen);
        Assert.False(recovered.IsHalfOpen);
    }

    [Fact]
    public void DefaultTransition_IsEmpty()
    {
        var empty = default(AxiosCircuitTransition);

        Assert.True(empty.IsEmpty);
        Assert.Null(empty.Host);
        Assert.False(empty.IsOpen);
        Assert.False(empty.IsHalfOpen);
    }
}
