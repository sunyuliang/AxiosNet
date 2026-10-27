using System.Collections.Concurrent;
using AxiosNet.Telemetry;

namespace AxiosNet.Pooling;

/// <summary>
/// 网络连接池：按 origin（scheme://host:port）隔离复用 Handler 与 HttpClient，
/// 控制单站点并发上限并后台驱逐长期空闲的节点；同一个池可被多个 AxiosClient 共享。
/// </summary>
public sealed class HttpConnectionPool : IDisposable
{
    private readonly ConcurrentDictionary<OriginKey, PooledEndpoint> _endpoints = new();
    private readonly ConnectionPoolOptions _options;
    private readonly CookieContainer? _cookies;
    private readonly Timer? _evictionTimer;
    private long _totalLeases;
    private int _disposed;

    public HttpConnectionPool(ConnectionPoolOptions? options = null)
    {
        // 克隆：调用方之后修改自己持有的 options 不影响已建成的池。
        _options = (options ?? new ConnectionPoolOptions()).Clone();
        ValidateOptions(_options);

        // 整个池共用一个 Cookie 容器：Cookie 的隔离维度是域/路径而不是端口。
        _cookies = _options.UseCookies ? new CookieContainer() : null;

        if (_options.IdleHandlerLifetime > TimeSpan.Zero && _options.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(EvictIdleEndpoints, state: null, dueTime: _options.EvictionInterval, period: _options.EvictionInterval);
        }
    }

    /// <summary>
    /// 构造期校验：这些值非法时底层 Handler 会在首次请求抛裸 <see cref="ArgumentOutOfRangeException"/>，
    /// 这里提前收敛到统一错误模型。
    /// </summary>
    internal static void ValidateOptions(ConnectionPoolOptions options, AxiosConfig? config = null)
    {
        if (options.MaxConnectionsPerServer <= 0)
        {
            throw AxiosException.InvalidOption($"MaxConnectionsPerServer 必须大于 0（当前 {options.MaxConnectionsPerServer}）。", config);
        }

        if (IsNegativeLimit(options.PooledConnectionLifetime))
        {
            throw AxiosException.InvalidOption("PooledConnectionLifetime 不能为负（0 表示连接不复用；不限生命周期请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (IsNegativeLimit(options.PooledConnectionIdleTimeout))
        {
            throw AxiosException.InvalidOption("PooledConnectionIdleTimeout 不能为负（0 表示不保留空闲连接；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.ConnectTimeout <= TimeSpan.Zero && options.ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("ConnectTimeout 必须大于 0；不限请用 Timeout.InfiniteTimeSpan。", config);
        }

        // 上限是 int.MaxValue 毫秒（约 24.85 天），越界时 BCL 抛裸 ArgumentOutOfRangeException。
        if (options.ConnectTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw AxiosException.InvalidOption(
                $"ConnectTimeout（{options.ConnectTimeout}）超出底层 Handler 上限（约 24.85 天）：请调小，"
                + "或不限请用 Timeout.InfiniteTimeSpan。",
                config);
        }

        if (options.MaxAutomaticRedirections <= 0)
        {
            throw AxiosException.InvalidOption("MaxAutomaticRedirections 必须大于 0；不跟随重定向请设 AllowAutoRedirect = false。", config);
        }

        if (IsNegativeLimit(options.IdleHandlerLifetime))
        {
            throw AxiosException.InvalidOption("IdleHandlerLifetime 不能为负（0 表示任何空闲节点立即可被驱逐；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.EvictionInterval < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption("EvictionInterval 不能为负（0 表示不启用后台驱逐定时器；要永不驱逐请设为 0 并手动调用 EvictIdleEndpoints）。", config);
        }

        // Timer 的 dueTime / period 上限是 uint.MaxValue-1 毫秒（约 49.7 天），越界时 BCL 抛裸 ArgumentOutOfRangeException。
        if (options.EvictionInterval > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw AxiosException.InvalidOption($"EvictionInterval（{options.EvictionInterval}）超出后台计时器上限（约 49.7 天）：请调小，或设为 0 改用手动驱逐。", config);
        }
    }

    /// <summary>是否为非法的负时长（<see cref="Timeout.InfiniteTimeSpan"/> 表示「不限」，放行）。</summary>
    private static bool IsNegativeLimit(TimeSpan value)
        => value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;

    /// <summary>池实际生效的选项（副本；修改它不会影响池）。</summary>
    public ConnectionPoolOptions Options => _options.Clone();

    /// <summary>池实际生效的选项（不克隆，仅限库内只读使用）。</summary>
    internal ConnectionPoolOptions EffectiveOptions => _options;

    /// <summary>从池中租用指定 URI 所属 origin 的连接节点。</summary>
    public PoolLease Lease(Uri requestUri) => Lease(requestUri, config: null);

    /// <summary>管道内部入口：携带请求配置，让池级错误也能指回调用方。</summary>
    internal PoolLease Lease(Uri requestUri, AxiosConfig? config)
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
            // 本次迭代是否真的新建并发布了节点。
            var createdNode = false;

            // 稳态只做一次哈希查找：容量统计与闭包分配只在需要新建节点时发生。
            if (!_endpoints.TryGetValue(origin, out var endpoint))
            {
                EnsureCapacity(origin, config);

                // 竞态下 GetOrAdd 只创建一次，落选的新建节点直接释放。
                var created = new PooledEndpoint(this, origin, _options, _cookies);
                endpoint = _endpoints.GetOrAdd(origin, created);
                if (!ReferenceEquals(created, endpoint))
                {
                    created.Dispose();
                }
                else
                {
                    createdNode = true;

                    // 节点创建是每个 origin 一次的事件，此刻没有持锁，可安全同步回调。
                    AxiosTelemetry.RecordEndpointCreated(origin.Host, origin.Port);
                }
            }

            if (endpoint.TryAcquire())
            {
                // 租用与关闭的竞态：池已关闭时归还并摘除节点，否则 Handler 与连接永久泄漏。
                if (Volatile.Read(ref _disposed) == 1)
                {
                    endpoint.DisposeWhenIdle = true;
                    endpoint.Release();
                    throw new ObjectDisposedException(nameof(HttpConnectionPool));
                }

                Interlocked.Increment(ref _totalLeases);

                // 只有成功拿到租约才计入指标。
                AxiosTelemetry.RecordLease(origin.Host, origin.Port, reused: !createdNode);
                return new PoolLease(this, endpoint, endpointReused: !createdNode);
            }

            // 节点恰好在获取锁之前被驱逐：移除后重试，会新建节点。
            _endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(origin, endpoint));
        }
    }

    /// <summary>异步租用：先检查取消令牌，其余与同步版本一致。</summary>
    public ValueTask<PoolLease> LeaseAsync(Uri requestUri, CancellationToken cancellationToken = default)
        => LeaseAsync(requestUri, config: null, cancellationToken);

    /// <summary>管道内部入口：语义同 <see cref="LeaseAsync(Uri, CancellationToken)"/>，额外携带请求配置。</summary>
    internal ValueTask<PoolLease> LeaseAsync(Uri requestUri, AxiosConfig? config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PoolLease>(Lease(requestUri, config));
    }

    internal void Return(PooledEndpoint endpoint)
    {
        endpoint.Release();
    }

    /// <summary>节点退租时把自己从字典摘掉（池关闭后的优雅回收路径）。</summary>
    internal void Forget(PooledEndpoint endpoint)
    {
        if (_endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(endpoint.Key, endpoint)))
        {
            AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose, endpoint.Key.Host, endpoint.Key.Port);
        }
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
    public int EvictIdleEndpoints() => EvictIdle(_options.IdleHandlerLifetime, int.MaxValue, AxiosTelemetry.EvictionReasonManual);

    /// <summary>新建节点前的容量检查：达到上限时先驱逐最久未使用的空闲节点，全部在途才拒绝。</summary>
    private void EnsureCapacity(OriginKey origin, AxiosConfig? config)
    {
        var max = _options.MaxEndpointCount;
        if (max <= 0 || _endpoints.ContainsKey(origin) || _endpoints.Count < max)
        {
            return;
        }

        if (EvictIdle(TimeSpan.Zero, limit: 1, AxiosTelemetry.EvictionReasonCapacity) > 0)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"连接池的 origin 节点数已达上限 {max}，且没有可驱逐的空闲节点（可能全部在途）。"
            + "可提高 ConnectionPoolOptions.MaxEndpointCount，或显式调用 Clear() 回收。",
            config);
    }

    /// <summary>驱逐空闲节点；<paramref name="reason"/> 只用于指标标签。</summary>
    /// <remarks>
    /// 两条路径都不物化字典快照：全量清扫顺序无关，直接枚举（<see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// 的枚举器是结构体，零分配）；限额驱逐（容量不足取 LRU）逐轮挑一个，轮数按「条数 + 限额」封顶。
    /// 后台定时器按周期跑这条路径，快照数组（每节点约 32 B）会随节点数线性变成常驻垃圾。
    /// </remarks>
    private int EvictIdle(TimeSpan minIdle, int limit, string reason)
    {
        // InfiniteTimeSpan 是 -1ms，不特判的话所有空闲节点都会被判成可驱逐。
        if (minIdle == Timeout.InfiniteTimeSpan || limit <= 0)
        {
            return 0;
        }

        var evicted = 0;
        var now = DateTime.UtcNow;
        List<PooledEndpoint>? pending = null;

        if (limit == int.MaxValue)
        {
            // 全量清扫：可驱逐性以锁内复核为准，顺序无关。
            foreach (var pair in _endpoints)
            {
                TryEvictIdle(pair.Key, pair.Value, minIdle, now, ref pending, ref evicted);
            }
        }
        else
        {
            // 限额驱逐：每轮挑「最久未使用且此刻可驱逐」的一个。轮数按「条数 + 限额」封顶：
            // 正常情况下每轮都能摘掉一个，只有在挑选与摘除之间被别的线程抢先时才会空转一轮
            //（限额驱逐只服务 EnsureCapacity，正常最多驱逐一两个）。
            var maxRounds = _endpoints.Count + limit;
            for (var round = 0; round < maxRounds && evicted < limit; round++)
            {
                var oldest = DateTime.MaxValue;
                var found = false;
                var candidateKey = default(OriginKey);
                PooledEndpoint? candidate = null;

                foreach (var pair in _endpoints)
                {
                    var endpoint = pair.Value;
                    if (endpoint.LastUsedUtc >= oldest || !IsEvictable(endpoint, minIdle, now))
                    {
                        continue;
                    }

                    oldest = endpoint.LastUsedUtc;
                    candidateKey = pair.Key;
                    candidate = endpoint;
                    found = true;
                }

                if (!found)
                {
                    break;
                }

                TryEvictIdle(candidateKey, candidate!, minIdle, now, ref pending, ref evicted);
            }
        }

        // 关闭 Handler 会断开该 origin 的全部连接、可能耗时，必须放在锁外。
        if (pending is not null)
        {
            foreach (var endpoint in pending)
            {
                endpoint.ReleaseResources();

                // 先释放再上报：监听者回调抛异常不该让节点漏掉 Handler。
                AxiosTelemetry.RecordEndpointEvicted(reason, endpoint.Key.Host, endpoint.Key.Port);
            }
        }

        return evicted;
    }

    /// <summary>锁内判定「此刻驱逐它等价于什么都没发生」。</summary>
    private static bool IsEvictable(PooledEndpoint endpoint, TimeSpan minIdle, DateTime now)
    {
        lock (endpoint.SyncRoot)
        {
            return !endpoint.IsDisposed
                && endpoint.ActiveLeases == 0
                && now - endpoint.LastUsedUtc >= minIdle;
        }
    }

    /// <summary>在锁内复核可驱逐性后摘除并计入待释放列表（释放仍在锁外）。</summary>
    private void TryEvictIdle(
        OriginKey key,
        PooledEndpoint endpoint,
        TimeSpan minIdle,
        DateTime now,
        ref List<PooledEndpoint>? pending,
        ref int evicted)
    {
        lock (endpoint.SyncRoot)
        {
            if (endpoint.IsDisposed
                || endpoint.ActiveLeases > 0
                || now - endpoint.LastUsedUtc < minIdle)
            {
                return;
            }

            // 先摘字典再置标记，两步都成功才收集起来、锁外统一释放。
            if (_endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(key, endpoint))
                && endpoint.MarkDisposed())
            {
                (pending ??= new List<PooledEndpoint>(1)).Add(endpoint);
                evicted++;
            }
        }
    }

    private void EvictIdleEndpoints(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            // 后台定时清扫（指标标签与手动驱逐不同）。
            EvictIdle(_options.IdleHandlerLifetime, int.MaxValue, AxiosTelemetry.EvictionReasonIdle);
        }
        catch
        {
            // 后台清理不应抛出未观察异常
        }
    }

    /// <summary>立即驱逐并释放所有 origin 节点（强制版，在途请求会被打断）。</summary>
    public void Clear()
    {
        foreach (var pair in _endpoints)
        {
            if (_endpoints.TryRemove(pair))
            {
                pair.Value.Dispose();
                AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonClear, pair.Key.Host, pair.Key.Port);
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

        // EndpointCount 与 Endpoints 必须来自同一次快照。
        return new ConnectionPoolStatistics(endpoints.Length, Interlocked.Read(ref _totalLeases), endpoints);
    }

    /// <summary>
    /// 生成 origin 池 key（scheme / host / port；主机名取 IDNA 形式，IPv6 回退 <see cref="Uri.Host"/>）。
    /// </summary>
    private static OriginKey BuildOrigin(Uri uri)
        => new(uri.Scheme, uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost, uri.Port);

    /// <summary>优雅关闭：空闲节点立即释放，仍有租约的节点等最后一条租约归还时回收。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _evictionTimer?.Dispose();

        List<PooledEndpoint>? pending = null;
        foreach (var pair in _endpoints)
        {
            var endpoint = pair.Value;
            lock (endpoint.SyncRoot)
            {
                // 锁内只做判定与摘除，释放放到锁外（与 EvictIdle 同一不变式）。
                if (endpoint.ActiveLeases == 0
                    && _endpoints.TryRemove(pair)
                    && endpoint.MarkDisposed())
                {
                    (pending ??= new List<PooledEndpoint>(1)).Add(endpoint);
                }
                else
                {
                    endpoint.DisposeWhenIdle = true;
                }
            }
        }

        if (pending is not null)
        {
            foreach (var endpoint in pending)
            {
                endpoint.ReleaseResources();
                AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose, endpoint.Key.Host, endpoint.Key.Port);
            }
        }
    }
}

/// <summary>origin 池 key：scheme / host / port 三段直接比较。</summary>
internal readonly struct OriginKey : IEquatable<OriginKey>
{
    public OriginKey(string scheme, string host, int port)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    public string Scheme { get; }

    public string Host { get; }

    public int Port { get; }

    public bool Equals(OriginKey other)
        => Port == other.Port
            && string.Equals(Scheme, other.Scheme, StringComparison.Ordinal)
            && string.Equals(Host, other.Host, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is OriginKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port);

    public override string ToString() => $"{Scheme}://{Host}:{Port}";
}
