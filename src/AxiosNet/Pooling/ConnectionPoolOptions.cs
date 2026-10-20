using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 连接池配置。
/// 底层按 origin(scheme://host:port) 复用 SocketsHttpHandler，
/// 每个 Handler 内部维护 TCP 连接池，本配置控制单服务器最大连接数、连接生命周期、
/// 空闲回收等，整体在 HttpClient 原生连接复用之上进一步按目标站点隔离与调优。
/// 需要插入自己的 <see cref="DelegatingHandler"/>（日志/签名/重试）时用 <see cref="HandlerFactory"/>，
/// 它会在保留上述池化能力的前提下替换每个 origin 的 Handler。
/// </summary>
public sealed class ConnectionPoolOptions
{
    /// <summary>每个 origin 允许的最大并发 TCP 连接数（默认 100）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>池化连接的最大存活时间，到期后优雅轮换（默认 10 分钟，支持 DNS 变更）。</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>连接在池中空闲多久后关闭（默认 2 分钟）。</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>建立 TCP/TLS 连接的超时时间（默认 15 秒）。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 某个 origin 的 Handler 空闲多久后从池中驱逐并释放（默认 5 分钟）。
    /// 设为 <see cref="TimeSpan.Zero"/> 表示任何空闲节点立即可被驱逐（手动或后台）。
    /// </summary>
    public TimeSpan IdleHandlerLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 空闲 Handler 驱逐扫描间隔（默认 30 秒）。
    /// <para>
    /// ≤ 0 时**不启动**后台驱逐定时器（只能手动调 <c>HttpConnectionPool.EvictIdleEndpoints()</c>）——
    /// 测试里常把 <see cref="IdleHandlerLifetime"/> 与本项同时设为 0 来彻底关闭后台线程。
    /// </para>
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否自动跟随重定向（默认 true）。</summary>
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>最大自动重定向次数（默认 5，对应 axios maxRedirects）。</summary>
    public int MaxAutomaticRedirections { get; set; } = 5;

    /// <summary>是否使用 Cookie 容器。</summary>
    /// <remarks>
    /// 打开后整个池共用<b>一个</b> <see cref="System.Net.CookieContainer"/>（不再是每个 origin 一个）：
    /// Cookie 的隔离维度是域和路径，不是端口——共用容器才能让
    /// <c>auth.example.com</c> 种下的会话 Cookie 被 <c>api.example.com</c> 带上，
    /// 与浏览器/axios 的 withCredentials 语义一致；不同站点之间仍由管家的域匹配规则隔离。
    /// </remarks>
    public bool UseCookies { get; set; }

    /// <summary>是否自动解压 gzip/deflate/br（默认 true）。</summary>
    public bool AutomaticDecompression { get; set; } = true;

    /// <summary>
    /// 池内 origin 节点（scheme://host:port）数量上限，默认 1024；0 或负数表示不限制。
    /// <para>
    /// 每个节点都会建一个 <c>SocketsHttpHandler</c>（持有连接池与定时器），请求目标若来自
    /// 用户输入（短链解析、webhook 回调、多租户自定义域名），节点数就成了可被放大的资源面。
    /// 达到上限时先驱逐最久未使用的空闲节点；一个都驱逐不掉（全部在途）则拒绝新 origin。
    /// </para>
    /// </summary>
    public int MaxEndpointCount { get; set; } = 1024;

    /// <summary>
    /// 代理。为 null 时池化为 <c>UseProxy = false</c>，即**不使用系统/环境代理**
    /// （与 Node axios 默认读 HTTP_PROXY 不同，避免环境变量带来的意外路由）；
    /// 需要显式走系统代理时设为 <see cref="System.Net.Http.HttpClient.DefaultProxy"/>。
    /// </summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>凭据（WithCredentials 时使用系统默认凭据）。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>自定义 HTTPS 证书校验回调。</summary>
    public RemoteCertificateValidationCallback? ServerCertificateCustomValidationCallback { get; set; }

    /// <summary>限制 TLS 协议版本。</summary>
    public SslProtocols? EnabledSslProtocols { get; set; }

    /// <summary>
    /// 自定义 Handler 工厂：每个 origin 调用一次，返回值用于该 origin 的所有请求。
    /// <para>
    /// 返回 <c>null</c> 表示使用库默认 Handler（等价于不设置）。想保留默认行为又插入自己的
    /// <see cref="DelegatingHandler"/> 时，用 <see cref="PooledHandlerContext.CreateDefaultHandler"/>
    /// 作为内层：<c>ctx => new MyHandler { InnerHandler = ctx.CreateDefaultHandler() }</c>。
    /// </para>
    /// <para>
    /// 约定：必须为每个 origin 返回<b>独立</b>实例（同一个 Handler 实例不能同时挂在多条管线/多个 origin 上），
    /// 且由连接池负责释放（收到 origin 驱逐或池 Dispose 时调用 <see cref="IDisposable.Dispose"/>）。
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
        HandlerFactory = HandlerFactory
    };
}
