using System.Net;
using AxiosNet.Pooling;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 与 <see cref="AxiosClient"/> 默认连接池<b>逐项对齐</b>的裸 Handler，用于「AxiosNet vs HttpClient」对照。
/// <para>必须显式对齐，否则测的是两个库的默认值差：<see cref="SocketsHttpHandler"/> 默认开
/// <c>UseCookies</c> / <c>UseProxy</c>，<see cref="ConnectionPoolOptions"/> 默认都关。</para>
/// <para>
/// 这份配置与 <c>test/AxiosNet.Tests/ConcurrencyPerformanceTests.CreateComparableHandler</c> 逐项一致：
/// 两处的比值要能互相印证，任何一边改了对齐口径都必须同步改另一边。
/// </para>
/// </summary>
internal static class ComparableHttpClientHandler
{
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
