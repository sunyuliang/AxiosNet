using System.Net;
using AxiosNet.Pooling;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 与 <see cref="AxiosClient"/> 默认连接池<b>逐项对齐</b>的裸 Handler，用于「AxiosNet vs HttpClient」对照。
/// <para>
/// 必须显式对齐，否则测的不是「谁快」，而是「两个库的默认值差多少」：
/// <see cref="SocketsHttpHandler"/> 默认 <c>UseCookies = true</c>、<c>UseProxy = true</c>，
/// 而 <see cref="ConnectionPoolOptions"/> 默认两者都关（<c>Proxy</c> 为 null 即 <c>UseProxy = false</c>）。
/// 2026-10-21 的消融实测（见 <c>ConcurrencyPerformanceTests</c> 的类注释）：仅把 HttpClient 侧这两项关掉，
/// 吞吐从 30,977 涨到 82,023 req/s（2.65 倍）；反向给 AxiosNet 打开，它从 54,642 掉到 36,455。
/// </para>
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
                ? DecompressionMethods.All
                : DecompressionMethods.None,
            UseCookies = defaults.UseCookies,
            UseProxy = defaults.Proxy is not null
        };
    }
}
