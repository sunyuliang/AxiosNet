using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 一条<b>命名传输档</b>的出口设置：代理、凭据、TLS / 客户端证书、建连回调与 Handler 工厂。
/// 用 <see cref="ConnectionPoolOptions.AddTransport"/> 登记到池上，再由
/// <see cref="AxiosConfig.ConnectionSettings.Transport"/> 按请求选择。
/// </summary>
/// <remarks>
/// <para>
/// 档是<b>一整套</b>出口设置，<b>不继承</b>默认档的任何传输字段：想在某个档上继续用默认档的代理，
/// 把同一个 <see cref="IWebProxy"/> 实例赋给它即可（按引用共享，零成本）。
/// 池级策略（连接数上限、连接生命周期、空闲驱逐、压缩、重定向、HTTP/2 与 ping）不在这里，由
/// <see cref="ConnectionPoolOptions"/> 统一决定。
/// </para>
/// <para>
/// 每个档在同一 origin 上有自己独立的 Handler、连接与 Cookie 容器，因此多出口 / 多租户 / 多证书可以共存于同一个池。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var pool = new HttpConnectionPool(new ConnectionPoolOptions()
///     .AddTransport("egress-a", new ConnectionTransportOptions { Proxy = proxyA })
///     .AddTransport("egress-b", new ConnectionTransportOptions { Proxy = proxyB, UseCookies = true }));
///
/// using var client = new AxiosClient(pool: pool);
/// var response = await client.GetAsync(c => { c.Url = "/orders"; c.Connection.Transport = "egress-b"; });
/// </code>
/// </example>
public sealed class ConnectionTransportOptions
{
    /// <summary>代理；为 <c>null</c> 时该档直连（不用系统 / 环境代理）。</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>代理 / 服务端凭据（与 <see cref="ConnectionPoolOptions.Credentials"/> 同义）。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>该档专用的服务端证书校验回调（覆盖默认档的同名项）。</summary>
    public RemoteCertificateValidationCallback? ServerCertificateCustomValidationCallback { get; set; }

    /// <summary>该档允许的 TLS 协议版本。</summary>
    public SslProtocols? EnabledSslProtocols { get; set; }

    /// <summary>
    /// TLS 选项扩展入口：在库构造的 <see cref="SslClientAuthenticationOptions"/> 上做最后调整，
    /// 客户端证书（mTLS）也在这里挂 —— 回调内的赋值不会被覆盖。
    /// </summary>
    public Action<SslClientAuthenticationOptions>? ConfigureSslOptions { get; set; }

    /// <summary>
    /// 该档的自定义建连回调（接管「把目标端点变成一条流」）：用于 SOCKS / 自定义拨号 / 固定源地址。
    /// 回调必须自己尊重取消令牌；TLS 仍由 Handler 在返回的流上完成。
    /// </summary>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }

    /// <summary>
    /// 该档的自定义 Handler 工厂（完全接管传输层）；为 <c>null</c> 时用库默认 Handler。
    /// 工厂拿到的 <see cref="PooledHandlerContext.Transport"/> 就是本档。
    /// </summary>
    public Func<PooledHandlerContext, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>
    /// 该档是否使用 Cookie 容器（默认 <c>false</c>）。每个档（以及每个 origin）各有一份独立容器，
    /// 容器容量沿用 <see cref="ConnectionPoolOptions.CookieCapacity"/> 等三项。
    /// </summary>
    public bool UseCookies { get; set; }

    internal ConnectionTransportOptions Clone() => (ConnectionTransportOptions)MemberwiseClone();
}
