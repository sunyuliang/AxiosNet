using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>按 <see cref="ConnectionPoolOptions"/> 构建库默认的 <see cref="SocketsHttpHandler"/>。</summary>
internal static class DefaultHandlerFactory
{
    /// <param name="o">池选项。</param>
    /// <param name="cookieContainer">池共享的 Cookie 容器（<see cref="ConnectionPoolOptions.UseCookies"/> 打开时非空）。</param>
    public static SocketsHttpHandler Create(ConnectionPoolOptions o, CookieContainer? cookieContainer = null)
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

            // h2 的并发度受服务端 MAX_CONCURRENT_STREAMS 限制，打开后按 MaxConnectionsPerServer 继续加连接。
            EnableMultipleHttp2Connections = o.EnableMultipleHttp2Connections,

            // h2 keep-alive：让中间设备（ALB / NAT / 防火墙）静默回收连接这件事在客户端可见，
            // 而不是等下一个请求撞上去才发现。h1 没有 ping 帧，这三项对它无影响。
            KeepAlivePingDelay = o.KeepAlivePingDelay,
            KeepAlivePingTimeout = o.KeepAlivePingTimeout,
            KeepAlivePingPolicy = o.KeepAlivePingPolicy,
            UseCookies = o.UseCookies,
            UseProxy = o.Proxy is not null,
            Proxy = o.Proxy,
            Credentials = o.Credentials
        };

        if (o.UseCookies)
        {
            handler.CookieContainer = cookieContainer ?? new CookieContainer();
        }

        if (o.ServerCertificateCustomValidationCallback is not null
            || o.EnabledSslProtocols is not null
            || o.ConfigureSslOptions is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = o.ServerCertificateCustomValidationCallback,
                EnabledSslProtocols = o.EnabledSslProtocols ?? SslProtocols.None
            };

            // 扩展入口放最后：回调内的赋值是最终值
            o.ConfigureSslOptions?.Invoke(handler.SslOptions);
        }

        return handler;
    }
}
