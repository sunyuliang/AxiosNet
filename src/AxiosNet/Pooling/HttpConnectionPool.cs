using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AxiosNet.Telemetry;

namespace AxiosNet.Pooling;

/// <summary>
/// 网络连接池：按 origin（scheme://host:port）隔离复用 Handler 与 HttpClient，
/// 控制单站点并发上限并后台驱逐长期空闲的节点；同一个池可被多个 AxiosClient 共享。
/// </summary>
public sealed class HttpConnectionPool : IDisposable
{
    /// <summary>
    /// 进程级命名池表：key 由调用方给（不做配置指纹），生命周期到 <see cref="Release"/> 为止。
    /// 表里的池不归任何 <see cref="AxiosClient"/> 所有——客户端释放不会关掉它。
    /// </summary>
    private static readonly ConcurrentDictionary<string, HttpConnectionPool> SharedPools = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<OriginKey, PooledEndpoint> _endpoints = new();
    private readonly ConnectionPoolOptions _options;
    private readonly object _cookieSync = new();
    private CookieContainer? _cookies;
    private readonly Timer? _evictionTimer;
    private long _totalLeases;
    private int _disposed;

    /// <summary>按选项建池（内部保存副本）；不传时用默认选项。选项非法时抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    public HttpConnectionPool(ConnectionPoolOptions? options = null)
    {
        // 克隆：调用方之后修改自己持有的 options 不影响已建成的池。
        _options = (options ?? new ConnectionPoolOptions()).Clone();
        ValidateOptions(_options);

        // 整个池共用一个 Cookie 容器。
        _cookies = _options.UseCookies ? CreateCookieContainer(_options) : null;

        if (_options.IdleHandlerLifetime > TimeSpan.Zero && _options.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(EvictIdleEndpoints, state: null, dueTime: _options.EvictionInterval, period: _options.EvictionInterval);
        }

        // 存活池数（进程级水位）：先校验后计数。
        AxiosTelemetry.EnterPool();
    }

    /// <summary>
    /// 构造期校验：这些值非法时统一收敛到 <c>ERR_BAD_OPTION_VALUE</c>。
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

        // 上限是 int.MaxValue 毫秒（约 24.85 天）。
        if (options.ConnectTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw AxiosException.InvalidOption(
                $"ConnectTimeout（{options.ConnectTimeout}）超出底层 Handler 上限（约 24.85 天）：请调小，"
                + "或不限请用 Timeout.InfiniteTimeSpan。",
                config);
        }

        // SocketsHttpHandler 只接受正数：没有「0 表示不限制」的写法。
        if (options.MaxResponseHeadersLength <= 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxResponseHeadersLength 必须大于 0（当前 {options.MaxResponseHeadersLength} KB）："
                + "它是响应头的绝对上限；需要更大余量请设成更大的 KB 值。", config);
        }

        if (options.MaxAutomaticRedirections <= 0)
        {
            throw AxiosException.InvalidOption("MaxAutomaticRedirections 必须大于 0；不跟随重定向请设 AllowAutoRedirect = false。", config);
        }

        // Cookie 容器容量三项只在真的建容器时才有意义：没开 UseCookies 时不校验，避免关掉 cookie 后还被无关项拦下。
        if (options.UseCookies)
        {
            if (options.CookieCapacity <= 0)
            {
                throw AxiosException.InvalidOption($"CookieCapacity 必须大于 0（当前 {options.CookieCapacity}）。", config);
            }

            if (options.CookiePerDomainCapacity <= 0 || options.CookiePerDomainCapacity > options.CookieCapacity)
            {
                throw AxiosException.InvalidOption(
                    $"CookiePerDomainCapacity 必须在 1 到 CookieCapacity（{options.CookieCapacity}）之间（当前 {options.CookiePerDomainCapacity}）："
                    + "只调小总容量时请一并调小单域上限。", config);
            }

            if (options.CookieMaxCookieSize <= 0)
            {
                throw AxiosException.InvalidOption($"CookieMaxCookieSize 必须大于 0（当前 {options.CookieMaxCookieSize}）。", config);
            }
        }

        // DecompressionMethods.All 在 BCL 里是 -1（每一位都亮），属合法的「全开」；
        // 超出 gzip / deflate / br 的位按配置错误拦下。
        const DecompressionMethods knownDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
        var requestedDecompression = options.Decompression == DecompressionMethods.All
            ? knownDecompression
            : options.Decompression;
        if ((requestedDecompression & ~knownDecompression) != 0)
        {
            throw AxiosException.InvalidOption(
                $"Decompression 含不支持的算法（当前 {options.Decompression}）：只允许 GZip / Deflate / Brotli 的组合或 All，"
                + "一个都不解压请设为 None（或把 AutomaticDecompression 设为 false）。", config);
        }

        if (IsNegativeLimit(options.IdleHandlerLifetime))
        {
            throw AxiosException.InvalidOption("IdleHandlerLifetime 不能为负（0 表示任何空闲节点立即可被驱逐；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.EvictionInterval < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption("EvictionInterval 不能为负（0 表示不启用后台驱逐定时器；要永不驱逐请设为 0 并手动调用 EvictIdleEndpoints）。", config);
        }

        // Timer 的 dueTime / period 上限是 uint.MaxValue-1 毫秒（约 49.7 天）。
        if (options.EvictionInterval > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw AxiosException.InvalidOption($"EvictionInterval（{options.EvictionInterval}）超出后台计时器上限（约 49.7 天）：请调小，或设为 0 改用手动驱逐。", config);
        }

        // 0 不是「不 ping」而是「立刻 ping」：不 ping 只有 InfiniteTimeSpan 一种写法。
        if (options.KeepAlivePingDelay <= TimeSpan.Zero && options.KeepAlivePingDelay != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("KeepAlivePingDelay 必须大于 0；不发 keep-alive ping 请设为 Timeout.InfiniteTimeSpan。", config);
        }

        if (options.KeepAlivePingTimeout <= TimeSpan.Zero && options.KeepAlivePingTimeout != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("KeepAlivePingTimeout 必须大于 0；不因 ping 超时断连请设为 Timeout.InfiniteTimeSpan。", config);
        }

        // 两者都由底层计时器表达，上限同样是 uint.MaxValue-1 毫秒。
        var pingLimit = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (options.KeepAlivePingDelay > pingLimit || options.KeepAlivePingTimeout > pingLimit)
        {
            throw AxiosException.InvalidOption(
                $"KeepAlivePingDelay / KeepAlivePingTimeout 超出计时器上限（约 49.7 天）：请调小，"
                + "或不发 ping 请设为 Timeout.InfiniteTimeSpan。",
                config);
        }

        // 枚举只认两个值，别的值底层会当成「不 ping」。
        if (options.KeepAlivePingPolicy is not (HttpKeepAlivePingPolicy.WithActiveRequests or HttpKeepAlivePingPolicy.Always))
        {
            throw AxiosException.InvalidOption(
                $"KeepAlivePingPolicy 取值非法（当前 {(int)options.KeepAlivePingPolicy}）："
                + "只支持 WithActiveRequests（只在有在途请求时 ping）与 Always。",
                config);
        }
    }

    /// <summary>是否为非法的负时长（<see cref="Timeout.InfiniteTimeSpan"/> 表示「不限」，放行）。</summary>
    private static bool IsNegativeLimit(TimeSpan value)
        => value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;

    // ---- 命名共享池 ----

    /// <summary>命名池表里的条目数（<see cref="GetOrCreate"/> 建出来、还没被 <see cref="Release"/> 的池）。</summary>
    public static int SharedCount => SharedPools.Count;

    /// <summary>
    /// 按 key 取进程级共享池：没有就建一条登记进去，有就直接返回同一条。
    /// </summary>
    /// <param name="key">池的名字（区分大小写、按原文比较）；同名的池共享 Handler 与连接。</param>
    /// <param name="options">
    /// 只在首次创建时生效。只想取池就传 <c>null</c>；同一 key 上再传一份**不同**的配置会抛
    /// <c>ERR_BAD_OPTION_VALUE</c>（引用成员按同一实例判定）。
    /// </param>
    public static HttpConnectionPool GetOrCreate(string key, ConnectionPoolOptions? options = null)
    {
        ValidateSharedKey(key);

        // 循环重试：登记与 Release 可能并发，落选后表里可能已没有这一项。
        while (true)
        {
            if (SharedPools.TryGetValue(key, out var existing))
            {
                EnsureSameOptions(key, existing, options);
                return existing;
            }

            // 先建后登记：选项合法性由构造函数统一收口（非法时抛 ERR_BAD_OPTION_VALUE）。
            var created = new HttpConnectionPool(options);

            if (SharedPools.TryAdd(key, created))
            {
                return created;
            }

            // 竞态落选：释放自己这条，再重走一遍复核配置。
            created.Dispose();
        }
    }

    /// <summary>
    /// 按 key 取已登记的池；没有则 <c>false</c>（不创建）。key 为 <c>null</c> 或空白同样返回 <c>false</c>——
    /// Try 语义不抛，与 <see cref="GetOrCreate"/> / <see cref="Release"/> 的严格校验区分开。
    /// </summary>
    public static bool TryGet(string key, [NotNullWhen(true)] out HttpConnectionPool? pool)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            pool = null;
            return false;
        }

        return SharedPools.TryGetValue(key, out pool);
    }

    /// <summary>
    /// 摘除命名池并优雅释放它（空闲节点立即释放，在途请求的节点等租约归还）；返回是否命中。
    /// 摘除后仍在用这条池的客户端发请求会拿到 <c>ERR_CANCELED</c>，只有直接调 <see cref="Lease(Uri)"/>
    /// 才会看到裸的 <see cref="ObjectDisposedException"/>。
    /// </summary>
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

    private static void ValidateSharedKey(string key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key), "命名池的 key 不能为 null。");
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
           && a.KeepAlivePingPolicy == b.KeepAlivePingPolicy
           && a.MaxEndpointCount == b.MaxEndpointCount
           && a.EnabledSslProtocols == b.EnabledSslProtocols
           && ReferenceEquals(a.ConnectCallback, b.ConnectCallback)
           && ReferenceEquals(a.Proxy, b.Proxy)
           && ReferenceEquals(a.Credentials, b.Credentials)
           && ReferenceEquals(a.ServerCertificateCustomValidationCallback, b.ServerCertificateCustomValidationCallback)
           && ReferenceEquals(a.ConfigureSslOptions, b.ConfigureSslOptions)
           && ReferenceEquals(a.HandlerFactory, b.HandlerFactory);

    /// <summary>池实际生效的选项（副本；修改它不会影响池）。</summary>
    public ConnectionPoolOptions Options => _options.Clone();

    /// <summary>
    /// 池当前使用的 Cookie 容器：<see cref="ConnectionPoolOptions.UseCookies"/> 打开时非空，否则为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 整个池共用这一份容器（按域 / 路径隔离，不按业务身份隔离），线程安全，可在请求进行中读写。
    /// 典型用途：启动时预置 cookie、排查某个 cookie 为什么没发出去；登出请用 <see cref="ClearCookies"/>
    /// （容器没有删除 API，只能整体替换，替换后这里读到的引用会变）。
    /// 开启容器后请求上的 <c>Cookie</c> 头与容器内容<em>合并</em>发送而不是覆盖；
    /// <see cref="Clear"/> 与 <see cref="Dispose"/> 都不会清空容器。
    /// </remarks>
    public CookieContainer? Cookies => Volatile.Read(ref _cookies);

    /// <summary>
    /// 清空池级 Cookie（登出）：换一份空容器并优雅回收现有 origin 节点，后续请求用新容器重建连接。
    /// </summary>
    /// <remarks>
    /// 只对开启 <see cref="ConnectionPoolOptions.UseCookies"/> 的池有效，未开启时无操作。
    /// <see cref="CookieContainer"/> 没有删除 API，所以实现上是整体换容器 + 回收节点（自定义 Handler 也会按新容器重新创建）。
    /// 不打断在途请求——它们可能仍带着旧 cookie 完成，节点在最后一条租约归还后销毁；
    /// 之前从 <see cref="Cookies"/> 拿到的引用随之失效，请重新读取。
    /// </remarks>
    public void ClearCookies()
    {
        if (Volatile.Read(ref _cookies) is null)
        {
            return;
        }

        // 与节点的「发布到字典」互斥：保证不会出现用旧容器创建、却在换容器之后才发布的节点。
        lock (_cookieSync)
        {
            if (Volatile.Read(ref _cookies) is null)
            {
                return;
            }

            Volatile.Write(ref _cookies, CreateCookieContainer(_options));
        }

        RetireAllEndpoints(AxiosTelemetry.EvictionReasonCookies);
    }

    /// <summary>按池选项建一个 Cookie 容器（容量三项与 BCL 默认值对齐，显式设置保证升级后行为稳定）。</summary>
    private static CookieContainer CreateCookieContainer(ConnectionPoolOptions options)
        => new()
        {
            // 先设总容量：单域上限必须不大于它（校验已在构造期保证）。
            Capacity = options.CookieCapacity,
            PerDomainCapacity = options.CookiePerDomainCapacity,
            MaxCookieSize = options.CookieMaxCookieSize
        };

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

            // 稳态只做一次哈希查找。
            if (!_endpoints.TryGetValue(origin, out var endpoint))
            {
                EnsureCapacity(origin, config);

                // 创建 Handler 可能较慢（自定义工厂），放在锁外；发布前在锁内复核容器没被 ClearCookies 换掉。
                var cookies = Volatile.Read(ref _cookies);
                var created = new PooledEndpoint(this, origin, _options, cookies);
                lock (_cookieSync)
                {
                    if (!ReferenceEquals(cookies, Volatile.Read(ref _cookies)))
                    {
                        // 用旧容器创建的节点作废（尚未发布、没有租约），重试时按新容器重建。
                        created.Dispose();
                        continue;
                    }

                    // 竞态下 GetOrAdd 只创建一次，落选的新建节点直接释放。
                    endpoint = _endpoints.GetOrAdd(origin, created);
                }

                if (!ReferenceEquals(created, endpoint))
                {
                    created.Dispose();
                }
                else
                {
                    createdNode = true;

                    // 节点创建事件：此刻没有持锁，可同步回调。
                    AxiosTelemetry.RecordEndpointCreated();
                }
            }

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

                // 只有成功拿到租约才计入指标。
                AxiosTelemetry.RecordLease(reused: !createdNode);
                return new PoolLease(this, endpoint, endpointReused: !createdNode);
            }

            // 节点恰好在获取锁之前被驱逐：移除后重试，会新建节点。
            _endpoints.TryRemove(new KeyValuePair<OriginKey, PooledEndpoint>(origin, endpoint));
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
            AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose);
        }
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
    /// <returns>本次驱逐的节点数。</returns>
    public int EvictIdleEndpoints() => EvictIdle(_options.IdleHandlerLifetime, int.MaxValue, AxiosTelemetry.EvictionReasonManual);

    /// <summary>新建节点前的容量检查：达到上限时先驱逐最久未使用的空闲节点，全部在途才拒绝（抛 <see cref="AxiosException.ErrPoolExhausted"/>，可观测、默认不自动重发）。</summary>
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

        // 运行期资源饱和按「资源错误」报：可观测、默认不自动重发、文案引导退避。
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

        // 单调时钟：墙钟回拨时 now - LastUsed 会变成负数，连 minIdle = 0 的容量驱逐都会失效。
        var now = Stopwatch.GetTimestamp();
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
            // 限额驱逐：每轮挑「最久未使用且此刻可驱逐」的一个，轮数按「条数 + 限额」封顶。
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

        // 关闭 Handler 会断开该 origin 的全部连接、可能耗时，必须放在锁外。
        if (pending is not null)
        {
            foreach (var endpoint in pending)
            {
                endpoint.ReleaseResources();

                // 先释放再上报：回调抛异常不能让节点漏掉 Handler。
                AxiosTelemetry.RecordEndpointEvicted(reason);
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

    /// <summary>
    /// 摘除全部 origin 节点：空闲的立即释放，仍有租约的等最后一条租约归还时自行销毁（不打断在途）。
    /// </summary>
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

            // 关闭 Handler 会断开该 origin 的全部连接、可能耗时，必须放在锁外。
            if (releaseNow)
            {
                endpoint.ReleaseResources();
            }

            AxiosTelemetry.RecordEndpointEvicted(reason);
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
                AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonClear);
            }
        }
    }

    /// <summary>取池的瞬时快照：逐节点在各自的锁内读取，各节点之间可能来自不同时刻。</summary>
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

        // 与存活池数成对：Dispose 幂等，不会重复减。
        AxiosTelemetry.ExitPool();

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
                AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose);
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
