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
    private readonly CookieContainer? _cookies;
    private readonly Timer? _evictionTimer;
    private long _totalLeases;
    private int _disposed;

    public HttpConnectionPool(ConnectionPoolOptions? options = null)
    {
        // 克隆：调用方之后修改自己持有的 options 对象，不应影响已建成的池
        _options = (options ?? new ConnectionPoolOptions()).Clone();
        ValidateOptions(_options);

        // 整个池共用一个 Cookie 容器：Cookie 的隔离维度是域/路径而不是端口，
        // 每个 origin 各持一个容器会把跨子域、换端口的会话直接切断。
        _cookies = _options.UseCookies ? new CookieContainer() : null;

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
    /// 构造期校验：这些值非法时 <see cref="SocketsHttpHandler"/> 会在「首次请求」抛裸
    /// <see cref="ArgumentOutOfRangeException"/>，既不是统一错误模型、也不在配置处暴露。
    /// </summary>
    internal static void ValidateOptions(ConnectionPoolOptions options, AxiosConfig? config = null)
    {
        if (options.MaxConnectionsPerServer <= 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxConnectionsPerServer 必须大于 0（当前 {options.MaxConnectionsPerServer}）。", config);
        }

        if (IsNegativeLimit(options.PooledConnectionLifetime))
        {
            throw AxiosException.InvalidOption(
                "PooledConnectionLifetime 不能为负（0 表示连接不复用；不限生命周期请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (IsNegativeLimit(options.PooledConnectionIdleTimeout))
        {
            throw AxiosException.InvalidOption(
                "PooledConnectionIdleTimeout 不能为负（0 表示不保留空闲连接；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.ConnectTimeout <= TimeSpan.Zero && options.ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("ConnectTimeout 必须大于 0；不限请用 Timeout.InfiniteTimeSpan。", config);
        }

        if (options.MaxAutomaticRedirections <= 0)
        {
            throw AxiosException.InvalidOption(
                "MaxAutomaticRedirections 必须大于 0；不跟随重定向请设 AllowAutoRedirect = false。", config);
        }

        if (IsNegativeLimit(options.IdleHandlerLifetime))
        {
            throw AxiosException.InvalidOption(
                "IdleHandlerLifetime 不能为负（0 表示任何空闲节点立即可被驱逐；不限请用 Timeout.InfiniteTimeSpan）。",
                config);
        }
    }

    /// <summary>
    /// 是否为非法的负时长。<see cref="Timeout.InfiniteTimeSpan"/>（-1ms）是 BCL 表达「不限」的标准值，
    /// 报错文案本身就在让调用方用它 —— 必须放行，否则「照文档改」只会换来另一个异常。
    /// </summary>
    private static bool IsNegativeLimit(TimeSpan value)
        => value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 池实际生效的选项（返回副本；修改它不会影响池，要调整请在构造池之前改）。
    /// </summary>
    public ConnectionPoolOptions Options => _options.Clone();

    /// <summary>
    /// 池实际生效的选项（不克隆）。<see cref="Options"/> 每次访问都会克隆一份，
    /// 请求管线里只是读两个值，没必要为它分配对象 —— 仅限库内只读使用。
    /// </summary>
    internal ConnectionPoolOptions EffectiveOptions => _options;

    /// <summary>从池中租用指定 URI 所属 origin 的连接节点。</summary>
    public PoolLease Lease(Uri requestUri) => Lease(requestUri, config: null);

    /// <summary>
    /// 管道内部入口：带上本次请求的合并配置，让池级错误（如 origin 节点数上限）也能通过
    /// <see cref="AxiosException.Config"/> 指回调用方的配置，而不是一个 Config 为 null 的异常。
    /// </summary>
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
            EnsureCapacity(origin, config);

            // 正常情况下 GetOrAdd 只创建一次；极端竞态下落选的新建节点直接释放。
            PooledEndpoint? created = null;
            var endpoint = _endpoints.GetOrAdd(origin, key =>
            {
                created = new PooledEndpoint(this, key, _options, _cookies);
                return created;
            });

            if (created is not null && !ReferenceEquals(created, endpoint))
            {
                created.Dispose();
            }

            if (endpoint.TryAcquire())
            {
                // 租用与关闭的竞态：GetOrAdd 可能在池已 Dispose 之后才插入新节点
                // （关闭迭代没看到它），此时 TryAcquire 仍会成功。不拦截的话，
                // 这条租约归还时节点既不在字典里、也没有 DisposeWhenIdle 标记，
                // Handler 与连接永久泄漏。带上标记再释放，让节点自行摘除。
                if (Volatile.Read(ref _disposed) == 1)
                {
                    endpoint.DisposeWhenIdle = true;
                    endpoint.Release();
                    throw new ObjectDisposedException(nameof(HttpConnectionPool));
                }

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

    /// <summary>节点退租时把自己从字典摘掉（池已关闭的优雅回收路径）。</summary>
    internal void Forget(PooledEndpoint endpoint)
    {
        _endpoints.TryRemove(new KeyValuePair<string, PooledEndpoint>(endpoint.Origin, endpoint));
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
    public int EvictIdleEndpoints() => EvictIdle(_options.IdleHandlerLifetime, int.MaxValue);

    /// <summary>
    /// 新建 origin 节点前的容量检查：达到 <see cref="ConnectionPoolOptions.MaxEndpointCount"/>
    /// 时先驱逐最久未使用的空闲节点，一个都驱逐不掉（全部在途）才拒绝。
    /// </summary>
    private void EnsureCapacity(string origin, AxiosConfig? config)
    {
        var max = _options.MaxEndpointCount;
        if (max <= 0 || _endpoints.Count < max || _endpoints.ContainsKey(origin))
        {
            return;
        }

        if (EvictIdle(TimeSpan.Zero, limit: 1) > 0)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"连接池的 origin 节点数已达上限 {max}，且没有可驱逐的空闲节点（可能全部在途）。"
            + "可提高 ConnectionPoolOptions.MaxEndpointCount，或显式调用 Clear() 回收。",
            config);
    }

    private int EvictIdle(TimeSpan minIdle, int limit)
    {
        var evicted = 0;
        var now = DateTime.UtcNow;
        List<PooledEndpoint>? pending = null;

        foreach (var pair in _endpoints)
        {
            if (evicted >= limit)
            {
                break;
            }

            var endpoint = pair.Value;
            lock (endpoint.SyncRoot)
            {
                if (endpoint.IsDisposed
                    || endpoint.ActiveLeases > 0
                    || now - endpoint.LastUsedUtc < minIdle)
                {
                    continue;
                }

                // 先摘字典再置标记：两步都成功后收集起来，锁外统一释放。
                // 若标记返回 false，说明该节点已被其它路径（Clear / Dispose）接管，不必再释放。
                if (_endpoints.TryRemove(new KeyValuePair<string, PooledEndpoint>(pair.Key, endpoint))
                    && endpoint.MarkDisposed())
                {
                    (pending ??= new List<PooledEndpoint>(1)).Add(endpoint);
                    evicted++;
                }
            }
        }

        // 关闭 Handler 会断开该 origin 的全部 TCP 连接、可能耗时：必须放在锁外。
        // 持锁释放会让同 origin 的 TryAcquire / GetStatistics / 另一次驱逐全部排队等它，
        // 而 EnsureCapacity 触发的驱逐恰恰跑在请求线程自己身上。
        if (pending is not null)
        {
            foreach (var endpoint in pending)
            {
                endpoint.ReleaseResources();
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

    /// <summary>
    /// 立即驱逐并释放所有 origin 节点（强制版，在途请求会被打断）。
    /// 优雅关闭请用 <see cref="Dispose"/>。
    /// </summary>
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

    /// <summary>
    /// 优雅关闭：空闲节点立即释放；仍有租约在途的节点先打标记，最后一条租约归还时自行回收。
    /// <para>
    /// 直接释放全部节点会让「客户端关闭时刚好有请求在飞」的调用方拿到
    /// <see cref="ObjectDisposedException"/> / 连接被重置——关闭不应该是一种数据破坏行为。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _evictionTimer?.Dispose();

        foreach (var pair in _endpoints)
        {
            var endpoint = pair.Value;
            lock (endpoint.SyncRoot)
            {
                if (endpoint.ActiveLeases == 0 && _endpoints.TryRemove(pair))
                {
                    endpoint.Dispose();
                }
                else
                {
                    endpoint.DisposeWhenIdle = true;
                }
            }
        }
    }
}
