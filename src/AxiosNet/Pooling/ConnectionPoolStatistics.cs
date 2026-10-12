namespace AxiosNet.Pooling;

/// <summary>连接池快照统计。</summary>
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
