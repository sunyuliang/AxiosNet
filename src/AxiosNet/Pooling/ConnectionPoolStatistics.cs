namespace AxiosNet.Pooling;

/// <summary>连接池快照统计。</summary>
/// <param name="EndpointCount">当前池内节点数（origin × 传输档维度）。</param>
/// <param name="TotalLeases">自池创建以来累计的租约次数（单调递增；当前活跃数见 <see cref="EndpointStatistics.ActiveLeases"/>）。</param>
/// <param name="Endpoints">各 origin 节点的明细快照。</param>
public sealed record ConnectionPoolStatistics(
    int EndpointCount,
    long TotalLeases,
    IReadOnlyList<EndpointStatistics> Endpoints);

/// <summary>单个 origin 节点的统计信息。</summary>
/// <param name="Origin">节点对应的地址：默认档为 <c>scheme://host:port</c>，命名档为 <c>scheme://host:port#档名</c>。</param>
/// <param name="ActiveLeases">当前未归还的租约数。</param>
/// <param name="LastUsedUtc">最近一次租用 / 归还的时刻（UTC）。</param>
/// <param name="IsDisposed">节点是否已被释放。</param>
/// <param name="Transport">节点所属的传输档名；默认档为空串。</param>
public sealed record EndpointStatistics(
    string Origin,
    int ActiveLeases,
    DateTime LastUsedUtc,
    bool IsDisposed,
    string Transport = "");
