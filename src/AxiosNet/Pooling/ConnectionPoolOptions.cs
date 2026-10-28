using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 连接池配置：单服务器最大连接数、连接生命周期、空闲回收等底层 Handler 选项。
/// 需要插入自己的 <see cref="DelegatingHandler"/>（日志/签名/重试）时用 <see cref="HandlerFactory"/>。
/// </summary>
public sealed class ConnectionPoolOptions
{
    /// <summary>每个 origin 允许的最大并发 TCP 连接数（默认 100）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>池化连接的最大存活时间（默认 10 分钟，到期优雅轮换）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不限。</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>连接在池中空闲多久后关闭（默认 2 分钟）；<see cref="Timeout.InfiniteTimeSpan"/> 表示一直保留。</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>建立 TCP/TLS 连接的超时时间（默认 15 秒）。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 某个 origin 的 Handler 空闲多久后从池中驱逐并释放（默认 5 分钟）。
    /// <see cref="TimeSpan.Zero"/> 表示任何空闲节点立即可被驱逐，<see cref="Timeout.InfiniteTimeSpan"/> 表示永不驱逐。
    /// </summary>
    public TimeSpan IdleHandlerLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>空闲 Handler 驱逐扫描间隔（默认 30 秒）；0 表示不启动后台驱逐定时器，只能手动调 <c>EvictIdleEndpoints()</c>。</summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否自动跟随重定向（默认 true）。</summary>
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>最大自动重定向次数（默认 5，对应 axios maxRedirects）。</summary>
    public int MaxAutomaticRedirections { get; set; } = 5;

    /// <summary>是否使用 Cookie 容器（整个池共用一个：Cookie 的隔离维度是域和路径，不是端口）。</summary>
    public bool UseCookies { get; set; }

    /// <summary>是否自动解压 gzip/deflate/br（默认 true）。</summary>
    public bool AutomaticDecompression { get; set; } = true;

    /// <summary>HTTP/2 下是否允许同一 origin 打开多条连接（默认 true）；只用 HTTP/1.1 时无影响。</summary>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// HTTP/2 keep-alive ping 的间隔（默认 <see cref="Timeout.InfiniteTimeSpan"/>，即不发 ping，与 <c>SocketsHttpHandler</c> 一致）。
    /// 只影响 HTTP/2 连接：h1 没有 ping 帧。
    /// </summary>
    /// <remarks>
    /// 打开它是对付「静默掐断」的手段：AWS ALB 默认 60 秒空闲断连、NAT 表 30–120 秒，客户端以为连接还在池里，
    /// 下次请求撞上已关闭的连接，要么多付一次 TLS 握手，要么在 h2 上收到 <c>GOAWAY</c> / 流重置。
    /// 发送 ping 的代价是一个 17 字节的帧，比一次重连便宜得多。默认关闭是因为它会改变线上行为：
    /// 链路上有不回应 <c>PING</c> 的代理 / 中间盒时，连接会被判死重连（<c>ERR_NETWORK</c> 变多）——
    /// 确认链路健康后再打开（常见取值 30 秒）。
    /// </remarks>
    public TimeSpan KeepAlivePingDelay { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 发出 ping 后多久没收到 <c>PING ACK</c> 就判定连接已死并关闭（默认 20 秒）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不因此断连。
    /// 只影响 HTTP/2 连接；必须小于 <see cref="KeepAlivePingDelay"/> 才谈得上「发现半死连接」。
    /// </summary>
    public TimeSpan KeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 何时发送 keep-alive ping（默认 <see cref="HttpKeepAlivePingPolicy.WithActiveRequests"/>，只在有在途请求时发）；
    /// 改成 <see cref="HttpKeepAlivePingPolicy.Always"/> 会在完全空闲时也维持连接（长轮询 / 低频高价值调用场景）。
    /// </summary>
    public HttpKeepAlivePingPolicy KeepAlivePingPolicy { get; set; } = HttpKeepAlivePingPolicy.WithActiveRequests;

    /// <summary>
    /// 池内 origin 节点数上限，默认 1024；0 或负数不限制（软上限：容量检查与插入非原子）。
    /// 全部在途且没有可驱逐的空闲节点时，新建节点抛 <see cref="AxiosException.ErrPoolExhausted"/>（请求没有发出，默认不自动重发）。
    /// </summary>
    public int MaxEndpointCount { get; set; } = 1024;

    /// <summary>代理；为 null 时不使用系统/环境代理，需要时设为 <see cref="System.Net.Http.HttpClient.DefaultProxy"/>。</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>凭据（WithCredentials 时使用系统默认凭据）。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>自定义 HTTPS 证书校验回调。</summary>
    public RemoteCertificateValidationCallback? ServerCertificateCustomValidationCallback { get; set; }

    /// <summary>限制 TLS 协议版本。</summary>
    public SslProtocols? EnabledSslProtocols { get; set; }

    /// <summary>TLS 选项扩展入口：在库构造的 <see cref="SslClientAuthenticationOptions"/> 上做最后调整（回调内的赋值不会被覆盖）。</summary>
    public Action<SslClientAuthenticationOptions>? ConfigureSslOptions { get; set; }

    /// <summary>自定义 Handler 工厂：每个 origin 调用一次，必须返回非 null 的独立实例，由池负责释放。</summary>
    public Func<PooledHandlerContext, HttpMessageHandler>? HandlerFactory { get; set; }

    public ConnectionPoolOptions Clone() => new()
    {
        MaxConnectionsPerServer = MaxConnectionsPerServer,
        PooledConnectionLifetime = PooledConnectionLifetime,
        PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
        ConnectTimeout = ConnectTimeout,
        IdleHandlerLifetime = IdleHandlerLifetime,
        EvictionInterval = EvictionInterval,
        AllowAutoRedirect = AllowAutoRedirect,
        MaxAutomaticRedirections = MaxAutomaticRedirections,
        UseCookies = UseCookies,
        AutomaticDecompression = AutomaticDecompression,
        EnableMultipleHttp2Connections = EnableMultipleHttp2Connections,
        KeepAlivePingDelay = KeepAlivePingDelay,
        KeepAlivePingTimeout = KeepAlivePingTimeout,
        KeepAlivePingPolicy = KeepAlivePingPolicy,
        MaxEndpointCount = MaxEndpointCount,
        Proxy = Proxy,
        Credentials = Credentials,
        ServerCertificateCustomValidationCallback = ServerCertificateCustomValidationCallback,
        EnabledSslProtocols = EnabledSslProtocols,
        ConfigureSslOptions = ConfigureSslOptions,
        HandlerFactory = HandlerFactory
    };
}
