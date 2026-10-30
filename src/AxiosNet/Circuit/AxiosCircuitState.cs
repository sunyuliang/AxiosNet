namespace AxiosNet.Circuit;

/// <summary>熔断器在某个计数维度（默认一个 <c>host:port</c>）上的状态。</summary>
public enum AxiosCircuitState
{
    /// <summary>闭合：请求正常放行，失败被计数。</summary>
    Closed,

    /// <summary>打开：请求不再发出，直接以 <c>ERR_CIRCUIT_OPEN</c> 失败，直到冷却期结束。</summary>
    Open,

    /// <summary>
    /// 半开：冷却期已过，按慢启动放行有限个探测请求；连续成功达 <see cref="CircuitBreaker.RecoverySuccesses"/> 次
    /// （0 / 1 时探测一次成功即可）才回到 <see cref="Closed"/>，期间任何一次失败都立刻回到 <see cref="Open"/> 并重新计时。
    /// </summary>
    HalfOpen
}

/// <summary>一次状态迁移（<see cref="CircuitBreaker.StateChanged"/> 的载荷）。</summary>
public readonly struct AxiosCircuitTransition
{
    /// <param name="host">发生迁移的计数维度（默认 <c>host:port</c>）。</param>
    /// <param name="previous">迁移前的状态。</param>
    /// <param name="current">迁移后的状态。</param>
    public AxiosCircuitTransition(string host, AxiosCircuitState previous, AxiosCircuitState current)
    {
        Host = host;
        Previous = previous;
        Current = current;
    }

    /// <summary>发生迁移的计数维度。</summary>
    public string Host { get; }

    /// <summary>迁移前的状态。</summary>
    public AxiosCircuitState Previous { get; }

    /// <summary>迁移后的状态。</summary>
    public AxiosCircuitState Current { get; }

    /// <summary>迁移后是否处于打开态（可直接用于日志 / 告警分级）。</summary>
    public bool IsOpen => Current == AxiosCircuitState.Open;

    /// <summary>迁移后是否已恢复（回到闭合态）。</summary>
    public bool IsClosed => Current == AxiosCircuitState.Closed;

    /// <summary>迁移后是否进入半开探测。</summary>
    public bool IsHalfOpen => Current == AxiosCircuitState.HalfOpen;

    /// <summary>是否是「空迁移」（默认值）：内部用它表示本次没有发生状态变化。</summary>
    public bool IsEmpty => Host is null;

    /// <inheritdoc />
    public override string ToString() => $"{Host}: {Previous} -> {Current}";
}
