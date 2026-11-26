using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>一条<b>命名传输档</b>的出口设置：代理、凭据、TLS / 客户端证书、建连回调与 Handler 工厂。</summary>
/// <remarks>
/// 档是一整套出口设置，不继承默认档的任何传输字段；把同一个 <see cref="IWebProxy"/> 实例赋给它即可共享。
/// 池级策略（连接数、生命周期、驱逐、压缩、重定向、HTTP/2）由 <see cref="ConnectionPoolOptions"/> 统一决定。
/// 每个档在同一 origin 上有独立的 Handler、连接与 Cookie 容器。
/// </remarks>
/// <example>
/// <code>
/// var pool = new HttpConnectionPool(new ConnectionPoolOptions()
///     .AddTransport("egress-a", new ConnectionTransportOptions { Proxy = proxyA })
///     .AddTransport("egress-b", new ConnectionTransportOptions { Proxy = proxyB, UseCookies = true }));
///
/// using var client = new AxiosClient(pool: pool);
/// var response = await client.GetAsync(c => { c.Url = "/orders"; c.Transport.Profile = "egress-b"; });
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

#if !NETSTANDARD2_0
    /// <summary>TLS 选项扩展入口：在库构造的 <see cref="SslClientAuthenticationOptions"/> 上做最后调整，客户端证书（mTLS）也在这里挂。</summary>
    public Action<SslClientAuthenticationOptions>? ConfigureSslOptions { get; set; }

    /// <summary>该档的自定义建连回调（SOCKS / 自定义拨号 / 固定源地址）；须尊重取消令牌，TLS 仍由 Handler 在返回的流上完成。</summary>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }
#endif
    // netstandard2.0 无 SslClientAuthenticationOptions / SocketsHttpConnectionContext，需完全接管时用 HandlerFactory。

    /// <summary>该档的自定义 Handler 工厂（完全接管传输层）；为 <c>null</c> 时用库默认 Handler。</summary>
    public Func<PooledHandlerContext, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>该档是否使用 Cookie 容器（默认 <c>false</c>）；每个档、每个 origin 各一份独立容器。</summary>
    public bool UseCookies { get; set; }

    /// <summary>浅拷贝（合并层用）。</summary>
    internal ConnectionTransportOptions Clone() => (ConnectionTransportOptions)MemberwiseClone();

    /// <summary>
    /// 两条档设置是否等价：标量逐项比，引用成员（代理 / 凭据 / 委托）按同一实例判定。
    /// </summary>
    /// <remarks>
    /// 与属性定义同文件维护：新增档字段时在紧邻位置补一行，避免「加字段但忘了参与等价判定」。
    /// </remarks>
    internal bool EqualsCore(ConnectionTransportOptions other)
    {
        var same = UseCookies == other.UseCookies
                   && EnabledSslProtocols == other.EnabledSslProtocols
                   && ReferenceEquals(Proxy, other.Proxy)
                   && ReferenceEquals(Credentials, other.Credentials)
                   && ReferenceEquals(ServerCertificateCustomValidationCallback, other.ServerCertificateCustomValidationCallback)
                   && ReferenceEquals(HandlerFactory, other.HandlerFactory);
#if !NETSTANDARD2_0
        same = same
               && ReferenceEquals(ConfigureSslOptions, other.ConfigureSslOptions)
               && ReferenceEquals(ConnectCallback, other.ConnectCallback);
#endif
        return same;
    }
}
