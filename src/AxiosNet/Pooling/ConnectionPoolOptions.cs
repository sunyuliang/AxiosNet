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

    /// <summary>池内 origin 节点数上限，默认 1024；0 或负数不限制（软上限：容量检查与插入非原子）。</summary>
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
        MaxEndpointCount = MaxEndpointCount,
        Proxy = Proxy,
        Credentials = Credentials,
        ServerCertificateCustomValidationCallback = ServerCertificateCustomValidationCallback,
        EnabledSslProtocols = EnabledSslProtocols,
        ConfigureSslOptions = ConfigureSslOptions,
        HandlerFactory = HandlerFactory
    };
}
