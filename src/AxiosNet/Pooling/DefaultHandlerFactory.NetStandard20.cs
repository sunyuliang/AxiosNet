#if NETSTANDARD2_0
using System.Security.Authentication;

namespace AxiosNet.Pooling;

internal static partial class DefaultHandlerFactory
{
    private static HttpMessageHandler CreateCore(
        ConnectionPoolOptions o,
        ConnectionTransportOptions transport,
        CookieContainer? cookieContainer)
    {
        // 兼容资产：HttpClientHandler 没有连接级生命周期、h2 ping、ConnectTimeout / ConnectCallback / SslOptions。
        // 这些池选项在该资产上由 HttpConnectionPool.ValidateOptions 拒绝非默认值（不静默忽略）；
        // 连接数上限与 TLS 协议版本两边都有，必须映射（否则是静默失效）。
        // 节点级能力（origin 隔离、节点数上限、空闲驱逐、租约、统计）仍由 HttpConnectionPool 自己保证。
        var handler = new HttpClientHandler
        {
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            AllowAutoRedirect = o.AllowAutoRedirect,
            MaxAutomaticRedirections = o.MaxAutomaticRedirections,

            // Framework 的 DecompressionMethods 没有 Brotli：只放行 gzip / deflate，
            // 免得把一个平台不认识的枚举值交给 HttpClientHandler 而在运行期抛参数异常。
            AutomaticDecompression = o.AutomaticDecompression
                ? o.Decompression & (DecompressionMethods.GZip | DecompressionMethods.Deflate)
                : DecompressionMethods.None,

            MaxResponseHeadersLength = o.MaxResponseHeadersLength,
            UseCookies = transport.UseCookies,
            UseProxy = transport.Proxy is not null,
            Proxy = transport.Proxy,
            Credentials = transport.Credentials,

            // SslProtocols.None = 交给操作系统选择；配置了具体版本时与 net6+ 的 SslOptions.EnabledSslProtocols 同义。
            SslProtocols = transport.EnabledSslProtocols ?? SslProtocols.None
        };

        if (transport.ServerCertificateCustomValidationCallback is { } certificateCallback)
        {
            // HttpClientHandler 的回调签名是 Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>，
            // 与配置里保存的 RemoteCertificateValidationCallback 不同，这里做一层转发。
            handler.ServerCertificateCustomValidationCallback =
                (request, certificate, chain, errors) => certificateCallback(request, certificate, chain, errors);
        }

        if (transport.UseCookies)
        {
            handler.CookieContainer = cookieContainer ?? new CookieContainer();
        }

        return handler;
    }
}
#endif
