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
            PooledConnectionLifetime = JitterLifetime(o.PooledConnectionLifetime),
            PooledConnectionIdleTimeout = o.PooledConnectionIdleTimeout,
            ConnectTimeout = o.ConnectTimeout,
            ConnectCallback = o.ConnectCallback,
            MaxResponseHeadersLength = o.MaxResponseHeadersLength,
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

    /// <summary>
    /// 给连接寿命加 ±10% 抖动：同一波流量里建立的连接年龄一致，若严格按同一时刻到期，
    /// 会在到期后的首个高峰集体重连（批量 TLS 握手 + 批量建连）。抖动只落在真正建 Handler 的那一刻，
    /// 不写回 <see cref="ConnectionPoolOptions"/> —— 命名池的配置一致性按原值比较。
    /// </summary>
    /// <remarks><see cref="TimeSpan.Zero"/>（不复用）与 <see cref="Timeout.InfiniteTimeSpan"/>（不限期）保持原样。</remarks>
    internal static TimeSpan JitterLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return lifetime;
        }

        var ticks = lifetime.Ticks * (0.9d + (Random.Shared.NextDouble() * 0.2d));

        // 极小正值（< 10 tick）乘 0.9 后截断会变成 0，而 0 在 SocketsHttpHandler 里表示「连接不复用」——
        // 语义从「短寿命」翻转成「永不复用」。至少保留 1 tick。
        if (ticks < 1d)
        {
            ticks = 1d;
        }

        // 超大寿命乘 1.1 会越过 long：夹到 TimeSpan 上限，别让抖动制造溢出。
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
