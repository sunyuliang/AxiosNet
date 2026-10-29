using System.Collections.Concurrent;
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

        // 存活池数（进程级水位）：先校验后计数，非法选项不该留下半条池。
        AxiosTelemetry.EnterPool();
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

        // SocketsHttpHandler 只接受正数：0 与负数都会在首次请求时抛裸 ArgumentOutOfRangeException（没有「0 表示不限制」的写法）。
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

        // DecompressionMethods.All 在 BCL 里是 -1（每一位都亮），属合法的「全开」；除此之外，超出 gzip / deflate / br 的位
        // 会被底层静默忽略 —— 静默失效比报错更难查，按配置错误拦下。
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

        // Timer 的 dueTime / period 上限是 uint.MaxValue-1 毫秒（约 49.7 天），越界时 BCL 抛裸 ArgumentOutOfRangeException。
        if (options.EvictionInterval > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw AxiosException.InvalidOption($"EvictionInterval（{options.EvictionInterval}）超出后台计时器上限（约 49.7 天）：请调小，或设为 0 改用手动驱逐。", config);
        }

        // h2 keep-alive：0 不是「不 ping」而是「立刻 ping」（会把连接打爆），不 ping 只有 InfiniteTimeSpan 一种写法。
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

        // 枚举只认两个值；别的值底层会当成「不 ping」，与调用方意图不符。
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
    /// 等价于「给这条池起个名字」，用来替代随手 <c>static readonly</c>，也省掉按请求建池的坑
    /// （见 <see cref="AxiosClient"/> 文档：一个站点一个实例，或共享同一条池）。
    /// </summary>
    /// <param name="key">池的名字（区分大小写、按原文比较）；同名的池共享 Handler 与连接。</param>
    /// <param name="options">
    /// 只在首次创建时生效。只想取池就传 <c>null</c>；同一 key 上再传一份**不同**的配置会抛
    /// <c>ERR_BAD_OPTION_VALUE</c>（引用成员按同一实例判定），避免「第二次传的配置被静默忽略」。
    /// </param>
    public static HttpConnectionPool GetOrCreate(string key, ConnectionPoolOptions? options = null)
    {
        ValidateSharedKey(key);

        // 循环而不是「取一次 + 补一次」：登记与 Release 可能并发，落选后不能假定表里还有这一项
        // （索引取值会抛 KeyNotFoundException），回到顶部重走一遍即可。
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

            // 竞态落选：自己这条必须释放（不释放会被后台驱逐定时器永久持有），再重走一遍复核配置。
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
    /// 摘除后仍在用这条池的客户端发请求会拿到 <c>ERR_CANCELED</c>（发送管线把池的
    /// <see cref="ObjectDisposedException"/> 翻译成统一错误模型），只有直接调 <see cref="Lease(Uri)"/>
    /// 才会看到裸的 <see cref="ObjectDisposedException"/>。它与自己 <c>Dispose</c> 掉一条共享池的
    /// 后果相同，属显式的收尾动作，不是自动回收。
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
    /// 两份选项是否等价：标量逐项比，引用成员（委托 / 代理 / 凭据）按同一实例判——
    /// 它们没有可靠的结构等价（两个长得一样的 lambda 也可能行为不同），宁可报错也不静默共用一条池。
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
                    AxiosTelemetry.RecordEndpointCreated();
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
                AxiosTelemetry.RecordLease(reused: !createdNode);
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
            AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonDispose);
        }
    }

    /// <summary>手动驱逐空闲超过 IdleHandlerLifetime 的 origin 节点。</summary>
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

        // 运行期资源饱和，按「资源错误」而非「配置错误」报（之前误用 ErrBadOptionValue，会杀掉默认重试）。
        // 与 ERR_QUEUE_TIMEOUT / ERR_CIRCUIT_OPEN 同哲学：可观测、默认不自动重发、文案引导退避。
        throw AxiosException.PoolExhausted(config, request: null, max);
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
                AxiosTelemetry.RecordEndpointEvicted(reason);
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
                AxiosTelemetry.RecordEndpointEvicted(AxiosTelemetry.EvictionReasonClear);
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

        // 与存活池数成对：Dispose 幂等（第二次进来上面就返回了），不会重复减。
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
