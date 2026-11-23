using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AxiosNet.Telemetry;

namespace AxiosNet.Pooling;

/// <summary>网络连接池：按 origin（scheme://host:port）隔离复用 Handler 与 HttpClient，控制单站点并发上限并后台驱逐长期空闲节点；可被多个 AxiosClient 共享。</summary>
public sealed class HttpConnectionPool : IDisposable
{
    /// <summary>进程级命名池表：key 由调用方给，生命周期到 <see cref="Release"/> 为止；表里的池不归任何 <see cref="AxiosClient"/> 所有。</summary>
    private static readonly ConcurrentDictionary<string, HttpConnectionPool> SharedPools = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<OriginKey, PooledEndpoint> _endpoints = new();
    private readonly ConnectionPoolOptions _options;

    /// <summary>命名传输档（构造期定型，之后只读）。</summary>
    private readonly Dictionary<string, ConnectionTransportOptions> _transports;

    /// <summary>默认档的出口设置：由池选项上的 Proxy / Credentials / TLS 等字段投影而来。</summary>
    private readonly ConnectionTransportOptions _defaultTransport;

    private readonly object _cookieSync = new();

    /// <summary>档名 → Cookie 容器（只有打开 UseCookies 的档才有条目；默认档名为空串）。</summary>
    private readonly ConcurrentDictionary<string, CookieContainer> _cookies = new(StringComparer.Ordinal);

    private readonly Timer? _evictionTimer;
    // 累计租约数（统计用，只增）。
    private long _totalLeases;
    // 0/1 释放标志。
    private int _disposed;

    /// <summary>默认传输档的档名（空串）。</summary>
    internal const string DefaultProfile = "";

    /// <summary>按选项建池（内部保存副本）；不传时用默认选项。选项非法时抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    public HttpConnectionPool(ConnectionPoolOptions? options = null)
    {
        _options = (options ?? new ConnectionPoolOptions()).Clone();
        ConnectionPoolOptionsValidator.Validate(_options);

        _transports = new Dictionary<string, ConnectionTransportOptions>(StringComparer.Ordinal);
        foreach (var pair in _options.Transports)
        {
            _transports[pair.Key] = pair.Value.Clone();
        }

        _defaultTransport = _options.ProjectDefaultTransport();

        if (_defaultTransport.UseCookies)
        {
            _cookies[DefaultProfile] = CreateCookieContainer(_options);
        }

        foreach (var pair in _transports)
        {
            if (pair.Value.UseCookies)
            {
                _cookies[pair.Key] = CreateCookieContainer(_options);
            }
        }

        if (_options.IdleHandlerLifetime > TimeSpan.Zero && _options.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(EvictIdleEndpoints, state: null, dueTime: _options.EvictionInterval, period: _options.EvictionInterval);
        }

        AxiosTelemetry.Dispatch.EnterPool();
    }

    // 命名共享池

    /// <summary>命名池表里的条目数（<see cref="GetOrCreate"/> 建出来、还没被 <see cref="Release"/> 的池）。</summary>
    public static int SharedCount => SharedPools.Count;

    /// <summary>按 key 取进程级共享池：没有就建一条登记进去，有就直接返回同一条。</summary>
    /// <param name="key">池的名字（区分大小写）；同名的池共享 Handler 与连接。</param>
    /// <param name="options">只在首次创建时生效；同一 key 再传不同配置会抛 <c>ERR_BAD_OPTION_VALUE</c>（引用成员按同一实例判定）。</param>
    public static HttpConnectionPool GetOrCreate(string key, ConnectionPoolOptions? options = null)
    {
        ValidateSharedKey(key);

        // 登记与 Release 可能并发，落选后表里可能已没有这一项。
        while (true)
        {
            if (SharedPools.TryGetValue(key, out var existing))
            {
                EnsureSameOptions(key, existing, options);
                return existing;
            }

            var created = new HttpConnectionPool(options);

            if (SharedPools.TryAdd(key, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    /// <summary>按 key 取已登记的池；没有（或 key 为 <c>null</c> / 空白）则返回 <c>false</c>，不创建也不抛。</summary>
    public static bool TryGet(string key, [NotNullWhen(true)] out HttpConnectionPool? pool)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            pool = null;
            return false;
        }

        return SharedPools.TryGetValue(key, out pool);
    }

    /// <summary>摘除命名池并优雅释放它（空闲节点立即释放，在途请求的节点等租约归还）；返回是否命中。摘除后在用这条池的客户端会拿到 <c>ERR_CANCELED</c>。</summary>
    public static bool Release(string key)
    {
        ValidateSharedKey(key);

        if (!SharedPools.TryRemove(key, out var pool))
        {
            return false;
        }

        pool.Dispose();
        return true;
    }

    // 共享池名必须非空。
    private static void ValidateSharedKey(string key)
    {
        if (key is null)
        {
            throw new System.ArgumentNullException(nameof(key), "命名池的 key 不能为 null。");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("命名池的 key 不能为空或空白。", nameof(key));
        }
    }

    /// <summary>同一 key 上重复传 options 时的校验：一致放行（幂等初始化），不一致明确报错。</summary>
    private static void EnsureSameOptions(string key, HttpConnectionPool pool, ConnectionPoolOptions? options)
    {
        if (options is null || SameOptions(pool._options, options))
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"命名池 '{key}' 已用另一份 ConnectionPoolOptions 创建：同一 key 上重复取池请不要传 options，"
            + "或让两次传入的配置完全一致（HandlerFactory / Proxy / Credentials / 证书回调等引用成员按同一实例判定）。");
    }

    /// <summary>
    /// 两份选项是否等价：标量逐项比，引用成员（委托 / 代理 / 凭据）按同一实例判。
    /// </summary>
    private static bool SameOptions(ConnectionPoolOptions a, ConnectionPoolOptions b)
        => a.MaxConnectionsPerServer == b.MaxConnectionsPerServer
           && a.PooledConnectionLifetime == b.PooledConnectionLifetime
           && a.PooledConnectionIdleTimeout == b.PooledConnectionIdleTimeout
           && a.ConnectTimeout == b.ConnectTimeout
#if !NETSTANDARD2_0
           && ReferenceEquals(a.ConnectCallback, b.ConnectCallback)
#endif
           && a.MaxResponseHeadersLength == b.MaxResponseHeadersLength
           && a.IdleHandlerLifetime == b.IdleHandlerLifetime
           && a.EvictionInterval == b.EvictionInterval
           && a.AllowAutoRedirect == b.AllowAutoRedirect
           && a.MaxAutomaticRedirections == b.MaxAutomaticRedirections
           && a.UseCookies == b.UseCookies
           && a.CookieCapacity == b.CookieCapacity
           && a.CookiePerDomainCapacity == b.CookiePerDomainCapacity
           && a.CookieMaxCookieSize == b.CookieMaxCookieSize
           && a.AutomaticDecompression == b.AutomaticDecompression
           && a.Decompression == b.Decompression
           && a.EnableMultipleHttp2Connections == b.EnableMultipleHttp2Connections
           && a.KeepAlivePingDelay == b.KeepAlivePingDelay
           && a.KeepAlivePingTimeout == b.KeepAlivePingTimeout
#if !NETSTANDARD2_0
           && a.KeepAlivePingPolicy == b.KeepAlivePingPolicy
#endif
           && a.MaxEndpointCount == b.MaxEndpointCount
           && a.EnabledSslProtocols == b.EnabledSslProtocols
           && ReferenceEquals(a.Proxy, b.Proxy)
           && ReferenceEquals(a.Credentials, b.Credentials)
           && ReferenceEquals(a.ServerCertificateCustomValidationCallback, b.ServerCertificateCustomValidationCallback)
#if !NETSTANDARD2_0
           && ReferenceEquals(a.ConfigureSslOptions, b.ConfigureSslOptions)
#endif
           && ReferenceEquals(a.HandlerFactory, b.HandlerFactory)
           && SameTransports(a.Transports, b.Transports);

    /// <summary>
    /// 传输档表是否等价：档名集合相同，且每个档的字段逐项相等（引用成员按同一实例）。
    /// </summary>
    private static bool SameTransports(
        IReadOnlyDictionary<string, ConnectionTransportOptions> a,
        IReadOnlyDictionary<string, ConnectionTransportOptions> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var other) || !SameTransport(pair.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    // 传输配置是否可视为同一档：代理按引用比较，其余逐项比较；不同就不能共池。
    private static bool SameTransport(ConnectionTransportOptions a, ConnectionTransportOptions b)
        => a.UseCookies == b.UseCookies
           && a.EnabledSslProtocols == b.EnabledSslProtocols
           && ReferenceEquals(a.Proxy, b.Proxy)
           && ReferenceEquals(a.Credentials, b.Credentials)
           && ReferenceEquals(a.ServerCertificateCustomValidationCallback, b.ServerCertificateCustomValidationCallback)
#if !NETSTANDARD2_0
           && ReferenceEquals(a.ConfigureSslOptions, b.ConfigureSslOptions)
           && ReferenceEquals(a.ConnectCallback, b.ConnectCallback)
#endif
           && ReferenceEquals(a.HandlerFactory, b.HandlerFactory);

    /// <summary>池实际生效的选项（副本；修改它不会影响池）。</summary>
    public ConnectionPoolOptions Options => _options.Clone();

    /// <summary>默认档当前使用的 Cookie 容器：默认档打开 <see cref="ConnectionTransportOptions.UseCookies"/> 时非空，否则为 <c>null</c>。</summary>
    /// <remarks>每个传输档各有一份容器（按域 / 路径隔离），线程安全；请求上的 <c>Cookie</c> 头与容器内容合并发送。</remarks>
    public CookieContainer? Cookies => GetCookies(DefaultProfile);

    /// <summary>取某个传输档的 Cookie 容器：未打开 <c>UseCookies</c> 或档名未登记时返回 <c>null</c>。</summary>
    /// <param name="transport">传输档名；<c>null</c> / 空串表示默认档。</param>
    public CookieContainer? GetCookies(string? transport)
    {
        var profile = transport ?? DefaultProfile;
        return _cookies.TryGetValue(profile, out var container) ? container : null;
    }

    /// <summary>清空<b>所有档</b>的 Cookie（登出）：换新的空容器并优雅回收现有 origin 节点。</summary>
    /// <remarks>
    /// 只对打开 <see cref="ConnectionTransportOptions.UseCookies"/> 的档有效。不打断在途请求；之前拿到的容器引用随之失效。
    /// </remarks>
    public void ClearCookies()
    {
        if (_cookies.IsEmpty)
        {
            return;
        }

        // 与节点的「发布到字典」互斥。
        lock (_cookieSync)
        {
            if (_cookies.IsEmpty)
            {
                return;
            }

            foreach (var profile in _cookies.Keys)
            {
                _cookies[profile] = CreateCookieContainer(_options);
            }
        }

        RetireAllEndpoints(AxiosTelemetry.EvictionReasonCookies);
    }

    /// <summary>按池选项建一个 Cookie 容器（容量三项与 BCL 默认值对齐，显式设置保证升级后行为稳定）。</summary>
    private static CookieContainer CreateCookieContainer(ConnectionPoolOptions options)
        => new()
        {
            Capacity = options.CookieCapacity,
            PerDomainCapacity = options.CookiePerDomainCapacity,
            MaxCookieSize = options.CookieMaxCookieSize
        };

    /// <summary>池实际生效的选项（不克隆，仅限库内只读使用）。</summary>
    internal ConnectionPoolOptions EffectiveOptions => _options;

    /// <summary>从池中租用指定 URI 所属 origin 的连接节点。</summary>
    public PoolLease Lease(Uri requestUri) => Lease(requestUri, config: null);

    /// <summary>预建 <paramref name="requestUri"/> 所属 origin 的池节点（Handler 与 HttpClient），<b>不建立 TCP 连接</b>。</summary>
    /// <param name="requestUri">绝对 URI；只用它的 scheme / host / port 决定 origin。</param>
    /// <param name="transport">传输档名；<c>null</c> / 空串表示默认档，未登记的档名抛 <c>ERR_BAD_OPTION_VALUE</c>。</param>
    /// <remarks>幂等，可重复调用；节点同样受 <see cref="ConnectionPoolOptions.MaxEndpointCount"/> 与空闲驱逐约束。池释放后抛 <see cref="ObjectDisposedException"/>。</remarks>
    public void Prewarm(Uri requestUri, string? transport = null)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(HttpConnectionPool));
        }

        if (requestUri is null)
        {
            throw new ArgumentNullException(nameof(requestUri));
        }

        if (!requestUri.IsAbsoluteUri)
        {
            throw AxiosException.InvalidOption(
                $"Prewarm 需要绝对 URI（当前 '{requestUri}'）：origin 由 scheme / host / port 决定，"
                + "相对地址无从判断要预建哪个节点。");
        }

        var profile = ResolveProfile(transport, config: null);
        EnsureEndpoint(BuildOrigin(requestUri, profile), profile, config: null, out _);
    }

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

        var profile = ResolveProfile(config);
        var origin = BuildOrigin(requestUri, profile);

        while (true)
        {
            var endpoint = EnsureEndpoint(origin, profile, config, out var createdNode);

            if (endpoint.TryAcquire())
            {
                // 租用与关闭的竞态：池已关闭时归还并摘除节点。
                if (Volatile.Read(ref _disposed) == 1)
                {
                    endpoint.DisposeWhenIdle = true;
                    endpoint.Release();
                    throw new ObjectDisposedException(nameof(HttpConnectionPool));
                }

                Interlocked.Increment(ref _totalLeases);

                AxiosTelemetry.Dispatch.RecordLease(reused: !createdNode);
                return new PoolLease(this, endpoint, endpointReused: !createdNode);
            }

            // 节点恰好在获取锁之前被驱逐：移除后重试，会新建节点。
            _endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(origin, endpoint));
        }
    }

    /// <summary>取 origin 对应的节点，没有就建一个（租约与预热共用）：建节点不建连。</summary>
    /// <param name="origin">节点 key。</param>
    /// <param name="profile">已解析的传输档名。</param>
    /// <param name="config">请求配置（池级错误指回调用方用）；预建节点时为 <c>null</c>。</param>
    /// <param name="created"><c>true</c> 表示本次真的新建并发布了节点（用于 <c>EndpointReused</c> 标签与新建指标）。</param>
    private PooledEndpoint EnsureEndpoint(OriginKey origin, string profile, AxiosConfig? config, out bool created)
    {
        created = false;

        if (_endpoints.TryGetValue(origin, out var existing))
        {
            return existing;
        }

        EnsureCapacity(origin, config);

        var transport = profile.Length == 0 ? _defaultTransport : _transports[profile];

        while (true)
        {
            var cookies = GetCookies(profile);

            // 建 Handler 可能较慢，放在锁外；发布前在锁内复核容器没被换掉。
            var candidate = new PooledEndpoint(this, origin, _options, transport, cookies);
            PooledEndpoint published;

            lock (_cookieSync)
            {
                if (!ReferenceEquals(cookies, GetCookies(profile)))
                {
                    candidate.Dispose();
                    continue;
                }

                published = _endpoints.GetOrAdd(origin, candidate);
            }

            if (!ReferenceEquals(candidate, published))
            {
                candidate.Dispose();
                return published;
            }

            created = true;

            AxiosTelemetry.Dispatch.RecordEndpointCreated();
            return published;
        }
    }

    /// <summary>异步租用：先检查取消令牌，其余与同步版本一致（实现上同步完成，建节点 / 调工厂都在调用线程）。</summary>
    public ValueTask<PoolLease> LeaseAsync(Uri requestUri, CancellationToken cancellationToken = default)
        => LeaseAsync(requestUri, config: null, cancellationToken);

    /// <summary>管道内部入口：语义同 <see cref="LeaseAsync(Uri, CancellationToken)"/>，额外携带请求配置。</summary>
    internal ValueTask<PoolLease> LeaseAsync(Uri requestUri, AxiosConfig? config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PoolLease>(Lease(requestUri, config));
    }

    /// <summary>归还租约（由 <see cref="PoolLease.Dispose"/> 调用）：只减占用计数，节点是否销毁由它自己的状态决定。</summary>
    internal void Return(PooledEndpoint endpoint)
    {
        endpoint.Release();
    }

    /// <summary>节点退租时把自己从字典摘掉（池关闭后的优雅回收路径）。</summary>
    internal void Forget(PooledEndpoint endpoint)
    {
        if (_endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(endpoint.Key, endpoint)))
        {
            AxiosTelemetry.Dispatch.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose);
        }
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
    /// <returns>本次驱逐的节点数。</returns>
    public int EvictIdleEndpoints() => EvictIdle(_options.IdleHandlerLifetime, int.MaxValue, AxiosTelemetry.EvictionReasonManual);

    /// <summary>新建节点前的容量检查：达到上限时先驱逐最久未用的空闲节点，全在途则抛 <see cref="AxiosException.ErrPoolExhausted"/>（默认不自动重发）。</summary>
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

        // 运行期资源饱和按「资源错误」报。
        throw AxiosException.PoolExhausted(config, request: null, max);
    }

    /// <summary>驱逐空闲节点；<paramref name="reason"/> 只用于指标标签。</summary>
    private int EvictIdle(TimeSpan minIdle, int limit, string reason)
    {
        // InfiniteTimeSpan 是 -1ms，不特判的话所有空闲节点都会被判成可驱逐。
        if (minIdle == Timeout.InfiniteTimeSpan || limit <= 0)
        {
            return 0;
        }

        var evicted = 0;

        // 单调时钟。
        var now = Stopwatch.GetTimestamp();
        List<PooledEndpoint>? pending = null;

        if (limit == int.MaxValue)
        {
            foreach (var pair in _endpoints)
            {
                TryEvictIdle(pair.Key, pair.Value, minIdle, now, ref pending, ref evicted);
            }
        }
        else
        {
            var maxRounds = _endpoints.Count + limit;
            for (var round = 0; round < maxRounds && evicted < limit; round++)
            {
                var oldest = long.MaxValue;
                var found = false;
                var candidateKey = default(OriginKey);
                PooledEndpoint? candidate = null;

                foreach (var pair in _endpoints)
                {
                    var endpoint = pair.Value;
                    var lastUsed = endpoint.LastUsedTimestamp;
                    if (lastUsed >= oldest || !IsEvictable(endpoint, minIdle, now))
                    {
                        continue;
                    }

                    oldest = lastUsed;
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

        // 关闭 Handler 会断开连接、可能耗时，放在锁外。
        if (pending is not null)
        {
            foreach (var endpoint in pending)
            {
                endpoint.ReleaseResources();

                // 先释放再上报，回调异常不会漏掉 Handler。
                AxiosTelemetry.Dispatch.RecordEndpointEvicted(reason);
            }
        }

        return evicted;
    }

    /// <summary>锁内判定节点此刻能否驱逐：未释放、无在途租约、空闲时间已达标。</summary>
    private static bool IsEvictable(PooledEndpoint endpoint, TimeSpan minIdle, long now)
    {
        lock (endpoint.SyncRoot)
        {
            return !endpoint.IsDisposed
                && endpoint.ActiveLeases == 0
                && endpoint.IdleSinceLastUse(now) >= minIdle;
        }
    }

    /// <summary>在锁内复核可驱逐性后摘除并计入待释放列表（释放仍在锁外）。</summary>
    private void TryEvictIdle(
        OriginKey key,
        PooledEndpoint endpoint,
        TimeSpan minIdle,
        long now,
        ref List<PooledEndpoint>? pending,
        ref int evicted)
    {
        lock (endpoint.SyncRoot)
        {
            if (endpoint.IsDisposed
                || endpoint.ActiveLeases > 0
                || endpoint.IdleSinceLastUse(now) < minIdle)
            {
                return;
            }

            // 先摘字典再置标记，锁外统一释放。
            if (_endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(key, endpoint))
                && endpoint.MarkDisposed())
            {
                (pending ??= new List<PooledEndpoint>(1)).Add(endpoint);
                evicted++;
            }
        }
    }

    // 定时清理：摘掉空闲超时的端点并释放。
    private void EvictIdleEndpoints(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            // 后台定时清扫（与手动驱逐指标标签不同）。
            EvictIdle(_options.IdleHandlerLifetime, int.MaxValue, AxiosTelemetry.EvictionReasonIdle);
        }
        catch
        {
            // 后台清理不应抛出未观察异常。
        }
    }

    /// <summary>摘除全部 origin 节点：空闲的立即释放，仍有租约的等最后一条租约归还时销毁（不打断在途）。</summary>
    private void RetireAllEndpoints(string reason)
    {
        foreach (var pair in _endpoints)
        {
            var endpoint = pair.Value;
            var releaseNow = false;

            lock (endpoint.SyncRoot)
            {
                if (endpoint.IsDisposed || !_endpoints.TryRemove(pair))
                {
                    continue;
                }

                if (endpoint.ActiveLeases == 0 && endpoint.MarkDisposed())
                {
                    releaseNow = true;
                }
                else
                {
                    endpoint.DisposeWhenIdle = true;
                }
            }

            // 关闭 Handler 会断开连接、可能耗时，放在锁外。
            if (releaseNow)
            {
                endpoint.ReleaseResources();
            }

            AxiosTelemetry.Dispatch.RecordEndpointEvicted(reason);
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
                AxiosTelemetry.Dispatch.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonClear);
            }
        }
    }

    /// <summary>取池的瞬时快照：逐节点在各自的锁内读取，各节点之间可能来自不同时刻。本方法是诊断入口，内部走 LINQ 与逐节点加锁，不要放在每请求路径上调用。</summary>
    public ConnectionPoolStatistics GetStatistics()
    {
        var endpoints = _endpoints
            .Select(p =>
            {
                var e = p.Value;
                lock (e.SyncRoot)
                {
                    return new EndpointStatistics(e.Origin, e.ActiveLeases, e.LastUsedUtc, e.IsDisposed, e.Key.Profile);
                }
            })
            .ToArray();

        // EndpointCount 与 Endpoints 来自同一次快照。
        return new ConnectionPoolStatistics(endpoints.Length, Interlocked.Read(ref _totalLeases), endpoints);
    }

    /// <summary>
    /// 生成池节点 key（scheme / host / port + 传输档；主机名取 IDNA 形式，IPv6 回退 <see cref="Uri.Host"/>）。
    /// </summary>
    private static OriginKey BuildOrigin(Uri uri, string profile)
        => new(uri.Scheme, uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost, uri.Port, profile);

    /// <summary>解析本次请求要用的传输档：未指定 = 默认档；未登记 → <c>ERR_BAD_OPTION_VALUE</c>（不静默回落）。</summary>
    private string ResolveProfile(AxiosConfig? config) => ResolveProfile(config?.ProfileOrNull, config);

    /// <summary>按档名解析传输档（<c>null</c> / 空串 = 默认档，未登记 → <c>ERR_BAD_OPTION_VALUE</c>）：请求与预建节点共用。</summary>
    private string ResolveProfile(string? requested, AxiosConfig? config)
    {
        if (string.IsNullOrEmpty(requested))
        {
            return DefaultProfile;
        }

        if (!_transports.ContainsKey(requested))
        {
            throw AxiosException.InvalidOption(
                $"传输档 '{requested}' 未在该连接池上登记：请用 ConnectionPoolOptions.AddTransport(\"{requested}\", ...) 登记，"
                + "或按请求不设 Transport.Profile 走默认档。已登记的档："
                + (_transports.Count == 0 ? "（无）" : string.Join(", ", _transports.Keys)) + "。",
                config);
        }

        return requested;
    }

    /// <summary>优雅关闭：空闲节点立即释放，仍有租约的节点等最后一条租约归还时回收。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AxiosTelemetry.Dispatch.ExitPool();

        _evictionTimer?.Dispose();

        List<PooledEndpoint>? pending = null;
        foreach (var pair in _endpoints)
        {
            var endpoint = pair.Value;
            lock (endpoint.SyncRoot)
            {
                // 锁内只做判定与摘除，释放放到锁外。
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
                AxiosTelemetry.Dispatch.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose);
            }
        }
    }
}

/// <summary>池节点 key：scheme / host / port 三段加传输档名（默认档为空串）。</summary>
internal readonly struct OriginKey : IEquatable<OriginKey>
{
    public OriginKey(string scheme, string host, int port, string profile)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        Profile = profile;
    }

    public string Scheme { get; }

    public string Host { get; }

    public int Port { get; }

    /// <summary>传输档名；空串 = 默认档（用 <see cref="ConnectionPoolOptions"/> 上的出口设置）。</summary>
    public string Profile { get; }

    /// <summary>默认档的 origin 文本（<c>scheme://host:port</c>），可直接拼成 Uri。</summary>
    public string BaseAddress => $"{Scheme}://{Host}:{Port}";

    // 按 (scheme, host, port, 档名) 判等，即「能不能复用连接」的粒度。
    public bool Equals(OriginKey other)
        => Port == other.Port
            && string.Equals(Scheme, other.Scheme, StringComparison.Ordinal)
            && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && string.Equals(Profile, other.Profile, StringComparison.Ordinal);

    // 装箱入口转强类型比较：字典查键会走这条。
    public override bool Equals(object? obj) => obj is OriginKey other && Equals(other);

    // 与 Equals 同一组字段。
    public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port, Profile);

    /// <summary>默认档输出 <c>scheme://host:port</c>；命名档追加 <c>#档名</c>（统计与诊断用）。</summary>
    public override string ToString()
        => Profile.Length == 0 ? BaseAddress : $"{BaseAddress}#{Profile}";
}
