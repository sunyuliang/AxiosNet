using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 连接池配置。底层按 origin(scheme://host:port) 复用 SocketsHttpHandler，
/// 本配置控制单服务器最大连接数、连接生命周期、空闲回收等。
/// 需要插入自己的 <see cref="DelegatingHandler"/>（日志/签名/重试）时用 <see cref="HandlerFactory"/>，
/// 它会在保留池化能力的前提下替换每个 origin 的 Handler。
/// </summary>
public sealed class ConnectionPoolOptions
{
    /// <summary>每个 origin 允许的最大并发 TCP 连接数（默认 100）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>
    /// 池化连接的最大存活时间，到期后优雅轮换（默认 10 分钟，支持 DNS 变更）。
    /// 不限生命周期请设为 <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 连接在池中空闲多久后关闭（默认 2 分钟）。
    /// 一直保留空闲连接请设为 <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>建立 TCP/TLS 连接的超时时间（默认 15 秒）。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 某个 origin 的 Handler 空闲多久后从池中驱逐并释放（默认 5 分钟）。
    /// 设为 <see cref="TimeSpan.Zero"/> 表示任何空闲节点立即可被驱逐（手动或后台）；
    /// 永不驱逐请设为 <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </summary>
    public TimeSpan IdleHandlerLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 空闲 Handler 驱逐扫描间隔（默认 30 秒）。≤ 0 时不启动后台驱逐定时器
    /// （只能手动调 <c>EvictIdleEndpoints()</c>）—— 测试里常与本项、<see cref="IdleHandlerLifetime"/> 一起设为 0。
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否自动跟随重定向（默认 true）。</summary>
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>最大自动重定向次数（默认 5，对应 axios maxRedirects）。</summary>
    public int MaxAutomaticRedirections { get; set; } = 5;

    /// <summary>是否使用 Cookie 容器。</summary>
    /// <remarks>
    /// 打开后整个池共用<b>一个</b> <see cref="System.Net.CookieContainer"/>：Cookie 的隔离维度是域和路径、不是端口，
    /// 共用才能让 <c>auth.example.com</c> 种下的会话被 <c>api.example.com</c> 带上（与浏览器/axios 一致）。
    /// </remarks>
    public bool UseCookies { get; set; }

    /// <summary>是否自动解压 gzip/deflate/br（默认 true）。</summary>
    public bool AutomaticDecompression { get; set; } = true;

    /// <summary>
    /// 池内 origin 节点数上限，默认 1024；0 或负数不限制。
    /// <para>
    /// 每个节点都会建一个 Handler（持有连接池与定时器），目标若来自用户输入（短链解析、多租户域名），
    /// 节点数就是可被放大的资源面。达到上限时先驱逐最久未使用的空闲节点，全在途则拒绝新 origin。
    /// </para>
    /// <para>
    /// 软上限：容量检查与字典插入非原子，逼近上限时突发多个新 origin 的并发请求可能让节点数短暂超出
    /// （超限量约等于并发建节点的线程数）；需要精确控制请自行串行化首批请求。
    /// </para>
    /// </summary>
    public int MaxEndpointCount { get; set; } = 1024;

    /// <summary>
    /// 代理。为 null 时池化为 <c>UseProxy = false</c>，即<b>不使用系统/环境代理</b>
    /// （与 Node axios 默认读 HTTP_PROXY 不同）；需要显式走系统代理时设为
    /// <see cref="System.Net.Http.HttpClient.DefaultProxy"/>。
    /// </summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>凭据（WithCredentials 时使用系统默认凭据）。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>自定义 HTTPS 证书校验回调。</summary>
    public RemoteCertificateValidationCallback? ServerCertificateCustomValidationCallback { get; set; }

    /// <summary>限制 TLS 协议版本。</summary>
    public SslProtocols? EnabledSslProtocols { get; set; }

    /// <summary>
    /// TLS 选项的扩展入口：库按 <see cref="ServerCertificateCustomValidationCallback"/> /
    /// <see cref="EnabledSslProtocols"/> 构造出 <see cref="SslClientAuthenticationOptions"/> 后调用，
    /// 用于设置这两个属性覆盖不到的部分（客户端证书、ALPN、加密套件策略等）。
    /// <para>回调内对 <c>EnabledSslProtocols</c> 的赋值即为最终值（库不会再覆盖）。</para>
    /// </summary>
    public Action<SslClientAuthenticationOptions>? ConfigureSslOptions { get; set; }

    /// <summary>
    /// 自定义 Handler 工厂：每个 origin 调用一次，返回值用于该 origin 的所有请求。
    /// <para>
    /// 必须返回非 null（否则抛 <see cref="InvalidOperationException"/>），且每个 origin 返回<b>独立</b>实例；
    /// 由连接池负责释放。想保留默认行为又插入自己的 <see cref="DelegatingHandler"/> 时：
    /// <c>ctx => new MyHandler { InnerHandler = ctx.CreateDefaultHandler() }</c>。
    /// </para>
    /// </summary>
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
        MaxEndpointCount = MaxEndpointCount,
        Proxy = Proxy,
        Credentials = Credentials,
        ServerCertificateCustomValidationCallback = ServerCertificateCustomValidationCallback,
        EnabledSslProtocols = EnabledSslProtocols,
        ConfigureSslOptions = ConfigureSslOptions,
        HandlerFactory = HandlerFactory
    };
}
