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
        _options = options ?? new ConnectionPoolOptions();

        if (_options.IdleHandlerLifetime > TimeSpan.Zero && _options.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(
                EvictIdleEndpoints,
                state: null,
                dueTime: _options.EvictionInterval,
                period: _options.EvictionInterval);
        }
    }

    public ConnectionPoolOptions Options => _options;

    /// <summary>从池中租用指定 URI 所属 origin 的连接节点。</summary>
    public PoolLease Lease(Uri requestUri)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

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

    /// <summary>异步租用（当前获取为轻量操作，保留异步签名以备扩展）。</summary>
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

    private static string BuildOrigin(Uri uri)
        => $"{uri.Scheme}://{uri.Host}:{uri.Port}";

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
