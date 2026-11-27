namespace AxiosNet.Pooling;

/// <summary>按 <see cref="ConnectionPoolOptions"/> 与一条传输档构建库默认的内层 Handler。</summary>
/// <remarks>net6.0 / net8.0 用 <c>SocketsHttpHandler</c>；netstandard2.0 退化为 <c>HttpClientHandler</c>，只映射两边都有的选项。</remarks>
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

    /// <summary>代理：调用方没给代理单独配凭据时，把实例级凭据补给它。</summary>
    /// <remarks>
    /// Handler 的 <c>Credentials</c> 只用于源站认证，代理 407 只读 <see cref="IWebProxy.Credentials"/>；
    /// 「代理地址 + 凭据」并排配置时不补这一层就会每次 407（且被归成 4xx，重试 / 熔断都按「目标服务器拒绝」处理）。
    /// </remarks>
    /// <param name="proxy">配置的代理；<c>null</c> 表示直连。</param>
    /// <param name="credentials">实例级凭据；<c>null</c> 时不补。</param>
    /// <returns>实际交给 Handler 的代理。</returns>
    internal static IWebProxy? ResolveProxy(IWebProxy? proxy, ICredentials? credentials)
        => proxy is not null && credentials is not null && proxy.Credentials is null
            ? new ProxyCredentialsFallback(proxy, credentials)
            : proxy;
}
