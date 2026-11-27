#if NETSTANDARD2_0
using System.Security.Authentication;

namespace AxiosNet.Pooling;

internal static partial class DefaultHandlerFactory
{
    /// <summary>兼容资产的默认档 Handler（<see cref="HttpClientHandler"/>）：只映射两边都有的项，连接级选项由构造期校验拒绝非默认值。</summary>
    /// <param name="o">池选项（连接数上限、重定向、解压、头长度上限）。</param>
    /// <param name="transport">出口设置（Cookie、代理、客户端证书）。</param>
    /// <param name="cookieContainer">共享 Cookie 容器；<c>null</c> 且启用 Cookie 时新建一个。</param>
    /// <returns>配置好的 Handler。</returns>
    private static HttpMessageHandler CreateCore(
        ConnectionPoolOptions o,
        ConnectionTransportOptions transport,
        CookieContainer? cookieContainer)
    {
        // 兼容资产：HttpClientHandler 没有连接级生命周期、h2 ping、ConnectTimeout 等，非默认值已在构造期拒绝。
        var handler = new HttpClientHandler
        {
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            AllowAutoRedirect = o.AllowAutoRedirect,
            MaxAutomaticRedirections = o.MaxAutomaticRedirections,

            // Framework 的 DecompressionMethods 无 Brotli，这里再掩一次作防御。
            AutomaticDecompression = o.AutomaticDecompression
                ? o.Decompression & (DecompressionMethods.GZip | DecompressionMethods.Deflate)
                : DecompressionMethods.None,

            MaxResponseHeadersLength = o.MaxResponseHeadersLength,
            UseCookies = transport.UseCookies,
            UseProxy = transport.Proxy is not null,
            Proxy = ResolveProxy(transport.Proxy, transport.Credentials),
            Credentials = transport.Credentials,

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
