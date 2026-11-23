#if !NETSTANDARD2_0
using System.Net.Security;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

internal static partial class DefaultHandlerFactory
{
    /// <summary>net6+ 资产的默认档 Handler（<see cref="SocketsHttpHandler"/>）：池选项逐项映射，连接寿命加抖动；SSL 仅在非默认时构造选项。</summary>
    /// <param name="o">池选项（连接数、寿命、空闲、建连超时、h2 并发与 keep-alive ping、解压）。</param>
    /// <param name="transport">出口设置（Cookie、代理、证书校验回调、SSL 协议版本与扩展钩子）。</param>
    /// <param name="cookieContainer">共享 Cookie 容器；<c>null</c> 且启用 Cookie 时新建一个。</param>
    /// <returns>配置好的 Handler。</returns>
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

            EnableMultipleHttp2Connections = o.EnableMultipleHttp2Connections,

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

            // 扩展入口放最后，回调内的赋值是最终值。
            transport.ConfigureSslOptions?.Invoke(handler.SslOptions);
        }

        return handler;
    }

    /// <summary>给连接寿命加 ±10% 抖动；抖动只在建 Handler 时生效，不写回 <see cref="ConnectionPoolOptions"/>。</summary>
    /// <remarks><see cref="TimeSpan.Zero"/>（不复用）与 <see cref="Timeout.InfiniteTimeSpan"/>（不限期）保持原样。</remarks>
    internal static TimeSpan JitterLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return lifetime;
        }

        var ticks = lifetime.Ticks * (0.9d + (PlatformCompat.NextJitter() * 0.2d));

        // 0 在 SocketsHttpHandler 里表示「连接不复用」：至少保留 1 tick。
        if (ticks < 1d)
        {
            ticks = 1d;
        }

        // 夹到 TimeSpan 上限。
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
#endif
