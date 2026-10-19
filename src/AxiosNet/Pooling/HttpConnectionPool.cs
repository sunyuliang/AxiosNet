using System.Collections.Concurrent;

namespace AxiosNet.Pooling;

/// <summary>
/// 网络连接池。
/// <para>
/// .NET 的 <see cref="SocketsHttpHandler"/> 自身已经会复用 TCP 连接，本连接池在其上再做一层
/// “按 origin 隔离的 Handler/Client 复用 + 并发上限 + 空闲驱逐”：
/// <list type="bullet">
/// <item>每个 scheme://host:port 对应一个调优过的 <see cref="SocketsHttpHandler"/>，所有请求共享，
/// 避免重复创建 Handler（错误使用 HttpClient 的典型 socket 耗尽问题）并复用其底层 TCP 连接；</item>
/// <item>通过 MaxConnectionsPerServer 控制每个站点的最大连接数，超出在连接层排队，整体吞吐更稳；</item>
/// <item>后台定时驱逐长期空闲的 origin 节点，释放文件描述符与内存；</item>
/// <item>同一个池可被多个 AxiosClient 共享。</item>
/// </list>
/// </para>
/// </summary>
public sealed class HttpConnectionPool : IDisposable
{
    private readonly ConcurrentDictionary<string, PooledEndpoint> _endpoints = new();
    private readonly ConnectionPoolOptions _options;
    private readonly Timer? _evictionTimer;
    private long _totalLeases;
    private int _disposed;

    public HttpConnectionPool(ConnectionPoolOptions? options = null)
    {
        // 克隆：调用方之后修改自己持有的 options 对象，不应影响已建成的池
        _options = (options ?? new ConnectionPoolOptions()).Clone();

        if (_options.IdleHandlerLifetime > TimeSpan.Zero && _options.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(
                EvictIdleEndpoints,
                state: null,
                dueTime: _options.EvictionInterval,
                period: _options.EvictionInterval);
        }
    }

    /// <summary>
    /// 池实际生效的选项（返回副本；修改它不会影响池，要调整请在构造池之前改）。
    /// </summary>
    public ConnectionPoolOptions Options => _options.Clone();

    /// <summary>从池中租用指定 URI 所属 origin 的连接节点。</summary>
    public PoolLease Lease(Uri requestUri)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(HttpConnectionPool));
        }

        if (requestUri is null || !requestUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Connection pool requires an absolute request URI.");
        }

        var origin = BuildOrigin(requestUri);

        while (true)
        {
            // 正常情况下 GetOrAdd 只创建一次；极端竞态下落选的新建节点直接释放。
            PooledEndpoint? created = null;
            var endpoint = _endpoints.GetOrAdd(origin, key =>
            {
                created = new PooledEndpoint(key, _options);
                return created;
            });

            if (created is not null && !ReferenceEquals(created, endpoint))
            {
                created.Dispose();
            }

            if (endpoint.TryAcquire())
            {
                Interlocked.Increment(ref _totalLeases);
                return new PoolLease(this, endpoint);
            }

            // 节点恰好在获取到锁之前被驱逐：移除后重试，会新建新节点
            _endpoints.TryRemove(new KeyValuePair<string, PooledEndpoint>(origin, endpoint));
        }
    }

    /// <summary>
    /// 异步租用：租用本身是轻量同步操作（字典查找 + 计数），
    /// 但会先检查 <paramref name="cancellationToken"/>——已取消的请求在租用前直接抛
    /// <see cref="OperationCanceledException"/>，不会占用池节点。
    /// </summary>
    public ValueTask<PoolLease> LeaseAsync(Uri requestUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PoolLease>(Lease(requestUri));
    }

    internal void Return(PooledEndpoint endpoint)
    {
        endpoint.Release();
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
    public int EvictIdleEndpoints()
    {
        var evicted = 0;
        var now = DateTime.UtcNow;
        foreach (var pair in _endpoints)
        {
            var endpoint = pair.Value;
            lock (endpoint.SyncRoot)
            {
                if (endpoint.IsDisposed
                    || endpoint.ActiveLeases > 0
                    || now - endpoint.LastUsedUtc < _options.IdleHandlerLifetime)
                {
                    continue;
                }

                if (_endpoints.TryRemove(new KeyValuePair<string, PooledEndpoint>(pair.Key, endpoint)))
                {
                    endpoint.Dispose();
                    evicted++;
                }
            }
        }

        return evicted;
    }

    private void EvictIdleEndpoints(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            EvictIdleEndpoints();
        }
        catch
        {
            // 后台清理不应抛出未观察异常
        }
    }

    /// <summary>立即驱逐并释放所有 origin 节点（通常在应用关闭时调用）。</summary>
    public void Clear()
    {
        foreach (var pair in _endpoints)
        {
            if (_endpoints.TryRemove(pair))
            {
                pair.Value.Dispose();
            }
        }
    }

    public ConnectionPoolStatistics GetStatistics()
    {
        var endpoints = _endpoints
            .Select(p =>
            {
                var e = p.Value;
                lock (e.SyncRoot)
                {
                    return new EndpointStatistics(e.Origin, e.ActiveLeases, e.LastUsedUtc, e.IsDisposed);
                }
            })
            .ToArray();

        return new ConnectionPoolStatistics(_endpoints.Count, Interlocked.Read(ref _totalLeases), endpoints);
    }

    /// <summary>
    /// 生成 origin 池 key（<c>scheme://host:port</c>）。
    /// <para>
    /// 主机名统一用 <see cref="Uri.IdnHost"/>（IDNA/punycode）而不是 <see cref="Uri.Host"/>：
    /// HTTP 主机头只能是 ASCII，unicode 主机名（<c>münchen.de</c>）发送前会被规范化为
    /// punycode（<c>xn--mnchen-3ya.de</c>）——服务端看到的两者字节完全相同（同一 DNS 名、
    /// 同一证书、同一 vhost），池 key 若保留原始拼写就会把同一个站点分裂成两个节点
    /// （连接配额各算一半、生命周期时钟分裂）。对 ASCII / IPv4 该属性与
    /// <see cref="Uri.Host"/> 一致。
    /// </para>
    /// <para>
    /// IPv6 是例外：<see cref="Uri.IdnHost"/> **不带**方括号（<c>::1</c>），而 Uri 解析与
    /// Host 头都要求括号形式，因此 IPv6 回退用 <see cref="Uri.Host"/>（带括号）。
    /// </para>
    /// </summary>
    private static string BuildOrigin(Uri uri)
        => $"{uri.Scheme}://{(uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost)}:{uri.Port}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _evictionTimer?.Dispose();
        Clear();
    }
}
