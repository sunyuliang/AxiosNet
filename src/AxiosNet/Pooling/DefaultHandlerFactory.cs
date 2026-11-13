namespace AxiosNet.Pooling;

/// <summary>按 <see cref="ConnectionPoolOptions"/> 与一条传输档构建库默认的内层 Handler。</summary>
/// <remarks>
/// net6.0 / net8.0 资产用 <c>SocketsHttpHandler</c>（连接级生命周期、并发上限、HTTP/2、ping、TLS 细粒度全可配）。
/// netstandard2.0 兼容资产退化为 <c>HttpClientHandler</c>，只映射两边都有的选项：
/// 连接数上限、响应头上限、压缩、重定向、Cookie、代理、凭据、TLS 协议版本、证书校验回调；
/// 连接生命周期 / ConnectTimeout / ConnectCallback / SslOptions / h2 ping 在该资产上不存在，降级清单见 README。
/// 两套实现分别在 <c>DefaultHandlerFactory.NetStandard20.cs</c> 与 <c>DefaultHandlerFactory.Upstream.cs</c>。
/// </remarks>
internal static partial class DefaultHandlerFactory
{
#if NETSTANDARD2_0
    /// <summary>按选项构建一个 Handler；需要 Cookie 但未给容器时新建一个。</summary>
    /// <param name="o">池选项。</param>
    /// <param name="transport">本次节点所属传输档（默认档由池从池选项投影得到）。</param>
    /// <param name="cookieContainer">该档该 origin 的 Cookie 容器（档打开 <see cref="ConnectionTransportOptions.UseCookies"/> 时非空）。</param>
    /// <returns>netstandard2.0 兼容资产返回 <c>HttpClientHandler</c>。</returns>
    public static HttpMessageHandler Create(
#else
    /// <summary>按选项构建一个 Handler；需要 Cookie 但未给容器时新建一个。</summary>
    /// <param name="o">池选项。</param>
    /// <param name="transport">本次节点所属传输档（默认档由池从池选项投影得到）。</param>
    /// <param name="cookieContainer">该档该 origin 的 Cookie 容器（档打开 <see cref="ConnectionTransportOptions.UseCookies"/> 时非空）。</param>
    /// <returns>net6.0 / net8.0 返回 <c>SocketsHttpHandler</c>。</returns>
    public static SocketsHttpHandler Create(
#endif
        ConnectionPoolOptions o,
        ConnectionTransportOptions transport,
        CookieContainer? cookieContainer = null)
        => CreateCore(o, transport, cookieContainer);
}
