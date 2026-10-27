namespace AxiosNet.Pooling;

/// <summary>连接池快照统计。</summary>
/// <param name="EndpointCount">当前池内 origin 节点数（scheme://host:port 维度）。</param>
/// <param name="TotalLeases">自池创建以来累计的租约次数（单调递增；当前活跃数见 <see cref="EndpointStatistics.ActiveLeases"/>）。</param>
/// <param name="Endpoints">各 origin 节点的明细快照。</param>
public sealed record ConnectionPoolStatistics(
    int EndpointCount,
    long TotalLeases,
    IReadOnlyList<EndpointStatistics> Endpoints);

/// <summary>单个 origin 节点的统计信息。</summary>
public sealed record EndpointStatistics(
    string Origin,
    int ActiveLeases,
    DateTime LastUsedUtc,
    bool IsDisposed);
