using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 按 <see cref="ConnectionPoolOptions"/> 构建库默认的 <see cref="SocketsHttpHandler"/>。
/// 连接池与 <see cref="PooledHandlerContext.CreateDefaultHandler"/> 共用同一套构建逻辑，
/// 保证"自定义 Handler 包装"与"默认行为"之间没有配置漂移。
/// </summary>
internal static class DefaultHandlerFactory
{
    public static SocketsHttpHandler Create(ConnectionPoolOptions o)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            PooledConnectionLifetime = o.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = o.PooledConnectionIdleTimeout,
            ConnectTimeout = o.ConnectTimeout,
            AllowAutoRedirect = o.AllowAutoRedirect,
            MaxAutomaticRedirections = o.MaxAutomaticRedirections,
            AutomaticDecompression = o.AutomaticDecompression
                ? DecompressionMethods.All
                : DecompressionMethods.None,
            UseCookies = o.UseCookies,
            UseProxy = o.Proxy is not null,
            Proxy = o.Proxy,
            Credentials = o.Credentials
        };

        if (o.UseCookies)
        {
            handler.CookieContainer = new CookieContainer();
        }

        if (o.ServerCertificateCustomValidationCallback is not null || o.EnabledSslProtocols is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = o.ServerCertificateCustomValidationCallback,
                EnabledSslProtocols = o.EnabledSslProtocols ?? SslProtocols.None
            };
        }

        return handler;
    }
}
