using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 连接池配置：每个 origin 的最大连接数、连接生命周期、空闲回收等底层 Handler 选项。
/// 需要插入自己的 <see cref="DelegatingHandler"/>（日志/签名/重试）时用 <see cref="HandlerFactory"/>。
/// </summary>
public sealed class ConnectionPoolOptions
{
    /// <summary>每个 origin 允许的最大并发 TCP 连接数（默认 100）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 100;

    /// <summary>
    /// 池化连接的最大存活时间（默认 10 分钟，到期优雅轮换）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不限。
    /// 实际落到 Handler 的值有 ±10% 抖动，本属性保持你设置的原值。
    /// </summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>连接在池中空闲多久后关闭（默认 2 分钟）；<see cref="Timeout.InfiniteTimeSpan"/> 表示一直保留。</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>建立 TCP/TLS 连接的超时时间（默认 15 秒）。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 自定义建连回调（直通 <see cref="SocketsHttpHandler.ConnectCallback"/>）：接管「把目标端点变成一条流」这一步，
    /// 为 <c>null</c> 时用 BCL 默认建连。
    /// </summary>
    /// <remarks>
    /// 回调必须自己尊重 <c>cancellationToken</c>（<see cref="ConnectTimeout"/> 到点会触发它）；返回的流由 Handler 持有并释放。
    /// TLS 仍由 Handler 在返回的流上完成（回调只负责明文传输），除非自行返回已加密的流。
    /// 仅作用于库默认 Handler；<see cref="HandlerFactory"/> 自建 Handler 时请自行映射。
    /// </remarks>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }

    /// <summary>
    /// 响应头体积上限（单位 KB，必须大于 0，默认 64，对应 <see cref="SocketsHttpHandler.MaxResponseHeadersLength"/>）。
    /// 超出时本次请求在响应头解析阶段失败并归一为 <c>ERR_NETWORK</c>（HTTP/1.1 与 HTTP/2 都生效）。
    /// 0 或负数在建池时就以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝。
    /// 仅作用于库默认 Handler；<see cref="HandlerFactory"/> 自建 Handler 时请自行映射。
    /// </summary>
    public int MaxResponseHeadersLength { get; set; } = 64;

    /// <summary>
    /// 某个 origin 的 Handler 空闲（无在途租约）多久后从池中驱逐并释放（默认 5 分钟）。
    /// <see cref="TimeSpan.Zero"/> 表示任何空闲节点都算可驱逐，<see cref="Timeout.InfiniteTimeSpan"/> 表示不按空闲时间驱逐
    /// （容量压力 / 关闭池时仍可能被收回）。注意 <c>Zero</c> 与 <c>Infinite</c> 都会让后台定时扫描不启动（见 <see cref="EvictionInterval"/>），
    /// 此时「可驱逐」只在容量压力或手动 <c>EvictIdleEndpoints()</c> 时生效。
    /// </summary>
    public TimeSpan IdleHandlerLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 空闲 Handler 驱逐扫描间隔（默认 30 秒）；<c>0</c> 表示不启动后台定时扫描（<see cref="IdleHandlerLifetime"/> 不大于 <see cref="TimeSpan.Zero"/> 时同样不启动），
    /// 此时仍可按容量压力驱逐或手动调 <c>EvictIdleEndpoints()</c>。
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
    /// 自动解压启用的算法集合（默认 <see cref="DecompressionMethods.All"/>，即 gzip / deflate / br 全开）。
    /// </summary>
    /// <remarks>
    /// 接受 <see cref="DecompressionMethods.GZip"/> / <see cref="DecompressionMethods.Deflate"/> / <see cref="DecompressionMethods.Brotli"/>
    /// 的任意组合，或 <see cref="DecompressionMethods.All"/>（BCL 里为 -1，等价于全开）；<see cref="DecompressionMethods.None"/>
    /// 表示一个都不解压，其余位在建池时以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝。
    /// 只在 <see cref="AutomaticDecompression"/> 为 true 时生效：客户端只宣告、也只解压这里列出的编码。
    /// </remarks>
    public DecompressionMethods Decompression { get; set; } = DecompressionMethods.All;

    /// <summary>HTTP/2 下是否允许同一 origin 打开多条连接（默认 true）；只用 HTTP/1.1 时无影响。</summary>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// HTTP/2 keep-alive ping 的间隔（默认 <see cref="Timeout.InfiniteTimeSpan"/>，即不发 ping）。
    /// 只影响 HTTP/2 连接：h1 没有 ping 帧。
    /// </summary>
    /// <remarks>
    /// 链路上的代理 / 中间盒不回应 <c>PING</c> 时连接会被判死重连（<c>ERR_NETWORK</c> 变多），确认链路健康后再打开。
    /// </remarks>
    public TimeSpan KeepAlivePingDelay { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 发出 ping 后多久没收到 <c>PING ACK</c> 就判定连接已死并关闭（默认 20 秒）；<see cref="Timeout.InfiniteTimeSpan"/> 表示不因此断连。
    /// 只影响 HTTP/2 连接，且只有 <see cref="KeepAlivePingDelay"/> 为有限值（真的会发 ping）时才有意义。
    /// </summary>
    /// <remarks>
    /// 一般取值小于 <see cref="KeepAlivePingDelay"/>；本项不做「必须小于」的校验，反过来配置不会报错。
    /// </remarks>
    public TimeSpan KeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 何时发送 keep-alive ping（默认 <see cref="HttpKeepAlivePingPolicy.WithActiveRequests"/>，只在有在途请求时发）；
    /// <see cref="HttpKeepAlivePingPolicy.Always"/> 在完全空闲时也维持连接。
    /// </summary>
    public HttpKeepAlivePingPolicy KeepAlivePingPolicy { get; set; } = HttpKeepAlivePingPolicy.WithActiveRequests;

    /// <summary>
    /// 池内 origin 节点数上限，默认 1024；0 或负数不限制（软上限：容量检查与插入非原子）。
    /// 全部在途且没有可驱逐的空闲节点时，新建节点抛 <see cref="AxiosException"/>（<c>ERR_POOL_EXHAUSTED</c>，请求没有发出，默认不自动重发）。
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

    /// <summary>
    /// 自定义 Handler 工厂：每个 origin 节点创建时调用一次（节点被驱逐 / 重建后会再次调用），
    /// 必须返回非 null 的独立实例，由池负责释放。
    /// </summary>
    public Func<PooledHandlerContext, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>
    /// 复制一份独立实例：池内部持有的是副本（<see cref="HttpConnectionPool.Options"/> 也返回副本）。
    /// 引用型字段（代理、委托）按引用共享，不做深拷贝。
    /// </summary>
    public ConnectionPoolOptions Clone() => new()
    {
        MaxConnectionsPerServer = MaxConnectionsPerServer,
        PooledConnectionLifetime = PooledConnectionLifetime,
        PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
        ConnectTimeout = ConnectTimeout,
        ConnectCallback = ConnectCallback,
        MaxResponseHeadersLength = MaxResponseHeadersLength,
        IdleHandlerLifetime = IdleHandlerLifetime,
        EvictionInterval = EvictionInterval,
        AllowAutoRedirect = AllowAutoRedirect,
        MaxAutomaticRedirections = MaxAutomaticRedirections,
        UseCookies = UseCookies,
        CookieCapacity = CookieCapacity,
        CookiePerDomainCapacity = CookiePerDomainCapacity,
        CookieMaxCookieSize = CookieMaxCookieSize,
        AutomaticDecompression = AutomaticDecompression,
        Decompression = Decompression,
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
