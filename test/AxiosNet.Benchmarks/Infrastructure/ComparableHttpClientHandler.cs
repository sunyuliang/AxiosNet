using System.Net;
using AxiosNet.Pooling;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 与 <c>SocketsHttpHandler</c> 默认连接池逐项对齐的裸 Handler，用于「AxiosNet vs HttpClient」对照：<c>UseCookies</c> / <c>UseProxy</c> 等默认值两边不同，必须显式对齐。
/// </summary>
internal static class ComparableHttpClientHandler
{
    /// <summary>按 <see cref="ConnectionPoolOptions"/> 的默认值逐项配置裸 handler；不对齐的话测到的是两套默认值的差，不是实现差。</summary>
    public static SocketsHttpHandler Create()
    {
        var defaults = new ConnectionPoolOptions();

        return new SocketsHttpHandler
        {
            MaxConnectionsPerServer = defaults.MaxConnectionsPerServer,
            PooledConnectionLifetime = defaults.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = defaults.PooledConnectionIdleTimeout,
            ConnectTimeout = defaults.ConnectTimeout,
            AllowAutoRedirect = defaults.AllowAutoRedirect,
            MaxAutomaticRedirections = defaults.MaxAutomaticRedirections,
            AutomaticDecompression = defaults.AutomaticDecompression
                ? defaults.Decompression
                : DecompressionMethods.None,
            UseCookies = defaults.UseCookies,
            UseProxy = defaults.Proxy is not null
        };
    }
}
