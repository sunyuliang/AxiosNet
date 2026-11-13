#if !NETSTANDARD2_0
using System.Net.Security;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

internal static partial class DefaultHandlerFactory
{
    private static SocketsHttpHandler CreateCore(
        ConnectionPoolOptions o,
        ConnectionTransportOptions transport,
        CookieContainer? cookieContainer)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            PooledConnectionLifetime = JitterLifetime(o.PooledConnectionLifetime),
            PooledConnectionIdleTimeout = o.PooledConnectionIdleTimeout,
            ConnectTimeout = o.ConnectTimeout,
            ConnectCallback = transport.ConnectCallback,
            MaxResponseHeadersLength = o.MaxResponseHeadersLength,
            AllowAutoRedirect = o.AllowAutoRedirect,
            MaxAutomaticRedirections = o.MaxAutomaticRedirections,
            AutomaticDecompression = o.AutomaticDecompression
                ? o.Decompression
                : DecompressionMethods.None,

            // h2 并发度受服务端 MAX_CONCURRENT_STREAMS 限制，打开后按 MaxConnectionsPerServer 继续加连接。
            EnableMultipleHttp2Connections = o.EnableMultipleHttp2Connections,

            // h2 keep-alive：三项只对 h2 生效，h1 没有 ping 帧。
            KeepAlivePingDelay = o.KeepAlivePingDelay,
            KeepAlivePingTimeout = o.KeepAlivePingTimeout,
            KeepAlivePingPolicy = o.KeepAlivePingPolicy,
            UseCookies = transport.UseCookies,
            UseProxy = transport.Proxy is not null,
            Proxy = transport.Proxy,
            Credentials = transport.Credentials
        };

        if (transport.UseCookies)
        {
            handler.CookieContainer = cookieContainer ?? new CookieContainer();
        }

        if (transport.ServerCertificateCustomValidationCallback is not null
            || transport.EnabledSslProtocols is not null
            || transport.ConfigureSslOptions is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = transport.ServerCertificateCustomValidationCallback,
                EnabledSslProtocols = transport.EnabledSslProtocols ?? SslProtocols.None
            };

            // 扩展入口放最后：回调内的赋值是最终值（客户端证书也在这里挂）。
            transport.ConfigureSslOptions?.Invoke(handler.SslOptions);
        }

        return handler;
    }

    /// <summary>
    /// 给连接寿命加 ±10% 抖动。抖动只落在真正建 Handler 的那一刻，
    /// 不写回 <see cref="ConnectionPoolOptions"/>。
    /// </summary>
    /// <remarks><see cref="TimeSpan.Zero"/>（不复用）与 <see cref="Timeout.InfiniteTimeSpan"/>（不限期）保持原样。</remarks>
    internal static TimeSpan JitterLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return lifetime;
        }

        var ticks = lifetime.Ticks * (0.9d + (PlatformCompat.NextJitter() * 0.2d));

        // 截断成 0 在 SocketsHttpHandler 里表示「连接不复用」：至少保留 1 tick。
        if (ticks < 1d)
        {
            ticks = 1d;
        }

        // 夹到 TimeSpan 上限，避免抖动制造溢出。
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
#endif
