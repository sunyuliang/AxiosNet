using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 连接池配置：每个 origin 的最大连接数、连接生命周期、空闲回收等底层 Handler 选项。
/// 需要插入自己的 <see cref="DelegatingHandler"/>（日志 / 签名 / 重试）时用 <see cref="HandlerFactory"/>。
/// </summary>
/// <remarks>
/// netstandard2.0 资产由 <c>HttpClientHandler</c> 承载，连接生命周期 / 空闲回收 / 建连超时 / h2 ping
/// 没有映射位置：设为非默认值会在建池时以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝，默认值也不生效。
/// </remarks>
public sealed class ConnectionPoolOptions
{
    // 默认值集中定义一处，属性初值与兼容资产校验共用。
    internal static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultPooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromSeconds(20);

    /// <summary>每个 origin 允许的最大并发 TCP 连接数（默认 100）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>
    /// 池化连接的最大存活时间（默认 10 分钟，到期优雅轮换）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不限。
    /// 落到 Handler 的值有 ±10% 抖动，本属性保持设置的原值。
    /// </summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = DefaultPooledConnectionLifetime;

    /// <summary>连接在池中空闲多久后关闭（默认 2 分钟）；<see cref="Timeout.InfiniteTimeSpan"/> 表示一直保留。</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = DefaultPooledConnectionIdleTimeout;

    /// <summary>建立 TCP/TLS 连接的超时时间（默认 15 秒）。</summary>
    public TimeSpan ConnectTimeout { get; set; } = DefaultConnectTimeout;

#if !NETSTANDARD2_0
    /// <summary>
    /// 自定义建连回调（直通 <see cref="SocketsHttpHandler.ConnectCallback"/>）：接管「把目标端点变成一条流」这一步，
    /// 为 <c>null</c> 时用 BCL 默认建连。
    /// </summary>
    /// <remarks>回调需尊重 <c>cancellationToken</c>；返回的流由 Handler 持有并释放，TLS 由其在该流上完成。</remarks>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }
#endif
    // netstandard2.0 无 SocketsHttpHandler，自定义拨号请用 HandlerFactory 接管整个 Handler。

    /// <summary>
    /// 响应头体积上限（KB，必须大于 0，默认 64）。超出时本次请求在响应头解析阶段失败并归一为 <c>ERR_NETWORK</c>；
    /// 0 或负数在建池时以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝。仅作用于库默认 Handler。
    /// </summary>
    public int MaxResponseHeadersLength { get; set; } = 64;

    /// <summary>
    /// 某个 origin 的 Handler 空闲（无在途租约）多久后从池中驱逐并释放（默认 5 分钟）。
    /// <see cref="TimeSpan.Zero"/> 表示任何空闲节点都可驱逐，<see cref="Timeout.InfiniteTimeSpan"/> 表示不按空闲时间驱逐。
    /// </summary>
    /// <remarks>两者都不启动后台扫描，此时驱逐只在容量压力或手动 <c>EvictIdleEndpoints()</c> 时发生。</remarks>
    public TimeSpan IdleHandlerLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 空闲 Handler 驱逐扫描间隔（默认 30 秒）；<c>0</c> 表示不启动后台定时扫描
    /// （<see cref="IdleHandlerLifetime"/> 不大于 <see cref="TimeSpan.Zero"/> 时同样不启动）。
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否自动跟随重定向（默认 true）。</summary>
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>最大自动重定向次数（默认 5，对应 axios maxRedirects）。</summary>
    public int MaxAutomaticRedirections { get; set; } = 5;

    /// <summary>是否使用 Cookie 容器（整个池共用一个）。</summary>
    public bool UseCookies { get; set; }

    /// <summary>
    /// Cookie 容器总条数上限（默认 300，与 <see cref="CookieContainer"/> 默认值一致）；超出时按容器规则淘汰最早的。
    /// 只在 <see cref="UseCookies"/> 打开时生效。
    /// </summary>
    public int CookieCapacity { get; set; } = 300;

    /// <summary>
    /// 单个域名的 cookie 上限（默认 20）；超出时淘汰该域名下最早的。不能大于 <see cref="CookieCapacity"/>。
    /// 只在 <see cref="UseCookies"/> 打开时生效。
    /// </summary>
    public int CookiePerDomainCapacity { get; set; } = 20;

    /// <summary>
    /// 单个 cookie 的最大字节数（默认 4096）；超长的 cookie 不会进入容器。
    /// 只在 <see cref="UseCookies"/> 打开时生效。
    /// </summary>
    public int CookieMaxCookieSize { get; set; } = 4096;

    /// <summary>
    /// 自动解压响应正文的总开关（默认 true）。为 false 时等价于 <see cref="DecompressionMethods.None"/>，
    /// <see cref="Decompression"/> 不再生效；开启时具体解哪些算法由 <see cref="Decompression"/> 决定。
    /// </summary>
    public bool AutomaticDecompression { get; set; } = true;

    /// <summary>
    /// 自动解压启用的算法集合，默认 <c>DecompressionMethods.All</c>（gzip / deflate / br；兼容资产只有 GZip | Deflate）。
    /// </summary>
    /// <remarks>
    /// 接受 GZip / Deflate / Brotli 的任意组合或 <c>All</c>；<c>None</c> 表示不解压，
    /// 其余位在建池时以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝。只在 <see cref="AutomaticDecompression"/> 为 true 时生效。
    /// </remarks>
    public DecompressionMethods Decompression { get; set; } =
#if NETSTANDARD2_0
        DecompressionMethods.GZip | DecompressionMethods.Deflate;
#else
        DecompressionMethods.All;
#endif

    /// <summary>HTTP/2 下是否允许同一 origin 打开多条连接（默认 true）；只用 HTTP/1.1 时无影响。</summary>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// HTTP/2 keep-alive ping 的间隔（默认 <see cref="Timeout.InfiniteTimeSpan"/>，即不发 ping）。
    /// 只影响 HTTP/2 连接：h1 没有 ping 帧。
    /// </summary>
    public TimeSpan KeepAlivePingDelay { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 发出 ping 后多久没收到 <c>PING ACK</c> 就判定连接已死并关闭（默认 20 秒）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不因此断连。
    /// 只影响 HTTP/2 连接，且 <see cref="KeepAlivePingDelay"/> 为有限值时才生效。
    /// </summary>
    /// <remarks>不校验与 <see cref="KeepAlivePingDelay"/> 的大小关系。</remarks>
    public TimeSpan KeepAlivePingTimeout { get; set; } = DefaultKeepAlivePingTimeout;

#if !NETSTANDARD2_0
    /// <summary>
    /// 何时发送 keep-alive ping（默认 <see cref="HttpKeepAlivePingPolicy.WithActiveRequests"/>，只在有在途请求时发）；
    /// <see cref="HttpKeepAlivePingPolicy.Always"/> 在完全空闲时也维持连接。
    /// </summary>
    public HttpKeepAlivePingPolicy KeepAlivePingPolicy { get; set; } = HttpKeepAlivePingPolicy.WithActiveRequests;
#endif
    // netstandard2.0 的 HttpClientHandler 没有 h2 keep-alive ping。

    /// <summary>
    /// 池内 origin 节点数上限，默认 1024；0 或负数不限制（软上限：容量检查与插入非原子）。
    /// 全部在途且没有可驱逐的空闲节点时，新建节点抛 <see cref="AxiosException"/>（<c>ERR_POOL_EXHAUSTED</c>，请求没有发出，默认不自动重发）。
    /// </summary>
    public int MaxEndpointCount { get; set; } = 1024;

    /// <summary>代理；为 null 时不使用系统/环境代理，需要时设为 <c>HttpClient.DefaultProxy</c>。</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>凭据（WithCredentials 时使用系统默认凭据）。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>自定义 HTTPS 证书校验回调。</summary>
    public RemoteCertificateValidationCallback? ServerCertificateCustomValidationCallback { get; set; }

    /// <summary>限制 TLS 协议版本。</summary>
    public SslProtocols? EnabledSslProtocols { get; set; }

#if !NETSTANDARD2_0
    /// <summary>TLS 选项扩展入口：在库构造的 <see cref="SslClientAuthenticationOptions"/> 上做最后调整（回调内的赋值不会被覆盖）。</summary>
    public Action<SslClientAuthenticationOptions>? ConfigureSslOptions { get; set; }
#endif
    // netstandard2.0 无 SslClientAuthenticationOptions；服务端证书校验用 ServerCertificateCustomValidationCallback，
    // 客户端证书用 HandlerFactory 自行挂载。

    /// <summary>
    /// 自定义 Handler 工厂：每个 origin 节点创建时调用一次（节点被驱逐 / 重建后会再次调用），
    /// 必须返回非 null 的独立实例，由池负责释放。
    /// </summary>
    /// <remarks>命名传输档可以在 <see cref="ConnectionTransportOptions.HandlerFactory"/> 上覆盖它。</remarks>
    public Func<PooledHandlerContext, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>命名传输档：档名 → 出口设置。默认（空档）用本类上的 Proxy / Credentials / TLS 等字段。</summary>
    private Dictionary<string, ConnectionTransportOptions>? _transports;

    /// <summary>
    /// 已登记的命名传输档（只读视图；档名区分大小写）。默认档不在其中 —— 它的设置就是本类上的那些字段。
    /// </summary>
    public IReadOnlyDictionary<string, ConnectionTransportOptions> Transports
        => _transports ?? EmptyTransports;

    /// <summary>空档表（只读包装）。</summary>
    private static readonly IReadOnlyDictionary<string, ConnectionTransportOptions> EmptyTransports =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, ConnectionTransportOptions>(
            new Dictionary<string, ConnectionTransportOptions>(StringComparer.Ordinal));

    /// <summary>
    /// 登记一条命名传输档：同一个池里可以并存多条出口（不同代理 / 不同客户端证书 / 不同 Cookie 会话），
    /// 由 <see cref="AxiosConfig.TransportSettings.Profile"/> 按请求选择。
    /// </summary>
    /// <param name="name">档名（区分大小写，不能为空或空白；空串保留给默认档）。</param>
    /// <param name="transport">该档的出口设置（保存副本）。</param>
    /// <returns>本实例，便于链式登记。</returns>
    /// <exception cref="ArgumentException">档名为空 / 空白，或同名档已登记。</exception>
    public ConnectionPoolOptions AddTransport(string name, ConnectionTransportOptions transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("传输档名不能为空或空白（默认档请直接改 ConnectionPoolOptions 上的字段）。", nameof(name));
        }

        _transports ??= new Dictionary<string, ConnectionTransportOptions>(StringComparer.Ordinal);

        if (!_transports.TryAdd(name, transport.Clone()))
        {
            throw new ArgumentException($"传输档 '{name}' 已登记：同一个池上不允许同名档（要改配置请在 AddTransport 之前改）。", nameof(name));
        }

        return this;
    }

    /// <summary>把本类上的出口字段投影成默认档。</summary>
    internal ConnectionTransportOptions ProjectDefaultTransport() => new()
    {
        Proxy = Proxy,
        Credentials = Credentials,
        ServerCertificateCustomValidationCallback = ServerCertificateCustomValidationCallback,
        EnabledSslProtocols = EnabledSslProtocols,
#if !NETSTANDARD2_0
        ConfigureSslOptions = ConfigureSslOptions,
        ConnectCallback = ConnectCallback,
#endif
        HandlerFactory = HandlerFactory,
        UseCookies = UseCookies
    };

    /// <summary>
    /// 复制一份独立实例：池内部持有的是副本（<see cref="HttpConnectionPool.Options"/> 也返回副本）。
    /// 引用型字段（代理、委托）按引用共享。
    /// </summary>
    /// <remarks>按指针浅拷贝再单独处理命名传输档字典，新增字段无需改动本方法。</remarks>
    public ConnectionPoolOptions Clone()
    {
        var copy = (ConnectionPoolOptions)MemberwiseClone();
        copy._transports = CloneTransports(_transports);
        return copy;
    }

    /// <summary>
    /// 两份池配置是否等价：标量逐项比，引用成员按同一实例判定，命名传输档按档名逐档比。
    /// </summary>
    /// <remarks>
    /// 与字段定义同文件维护：新增池字段时在紧邻位置补一行。
    /// 漏改的后果是静默的——配置已变却复用旧池时不会报错。
    /// </remarks>
    internal bool EqualsCore(ConnectionPoolOptions other)
    {
        var same = MaxConnectionsPerServer == other.MaxConnectionsPerServer
                   && PooledConnectionLifetime == other.PooledConnectionLifetime
                   && PooledConnectionIdleTimeout == other.PooledConnectionIdleTimeout
                   && ConnectTimeout == other.ConnectTimeout
                   && MaxResponseHeadersLength == other.MaxResponseHeadersLength
                   && IdleHandlerLifetime == other.IdleHandlerLifetime
                   && EvictionInterval == other.EvictionInterval
                   && AllowAutoRedirect == other.AllowAutoRedirect
                   && MaxAutomaticRedirections == other.MaxAutomaticRedirections
                   && UseCookies == other.UseCookies
                   && CookieCapacity == other.CookieCapacity
                   && CookiePerDomainCapacity == other.CookiePerDomainCapacity
                   && CookieMaxCookieSize == other.CookieMaxCookieSize
                   && AutomaticDecompression == other.AutomaticDecompression
                   && Decompression == other.Decompression
                   && EnableMultipleHttp2Connections == other.EnableMultipleHttp2Connections
                   && KeepAlivePingDelay == other.KeepAlivePingDelay
                   && KeepAlivePingTimeout == other.KeepAlivePingTimeout
                   && MaxEndpointCount == other.MaxEndpointCount
                   && ReferenceEquals(Proxy, other.Proxy)
                   && ReferenceEquals(Credentials, other.Credentials)
                   && ReferenceEquals(ServerCertificateCustomValidationCallback, other.ServerCertificateCustomValidationCallback)
                   && EnabledSslProtocols == other.EnabledSslProtocols
                   && ReferenceEquals(HandlerFactory, other.HandlerFactory);
#if !NETSTANDARD2_0
        same = same
               && ReferenceEquals(ConnectCallback, other.ConnectCallback)
               && KeepAlivePingPolicy == other.KeepAlivePingPolicy
               && ReferenceEquals(ConfigureSslOptions, other.ConfigureSslOptions);
#endif
        return same && TransportsEqual(_transports, other._transports);
    }

    /// <summary>命名传输档表是否等价：档名集合相同，且每个档的设置逐项相等。</summary>
    private static bool TransportsEqual(
        Dictionary<string, ConnectionTransportOptions>? left,
        Dictionary<string, ConnectionTransportOptions>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) || !pair.Value.EqualsCore(other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>深拷贝命名传输档字典（键区分大小写）。</summary>
    private static Dictionary<string, ConnectionTransportOptions>? CloneTransports(
        Dictionary<string, ConnectionTransportOptions>? source)
    {
        if (source is null)
        {
            return null;
        }

        var copy = new Dictionary<string, ConnectionTransportOptions>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source)
        {
            copy[pair.Key] = pair.Value.Clone();
        }

        return copy;
    }
}
