namespace AxiosNet.Pooling;

/// <summary>连接池创建单个 origin 的 Handler 时传入的上下文，配合 <see cref="ConnectionTransportOptions.HandlerFactory"/> / <see cref="ConnectionPoolOptions.HandlerFactory"/> 使用。</summary>
/// <example>
/// <code>
/// var pool = new HttpConnectionPool(new ConnectionPoolOptions
/// {
///     HandlerFactory = ctx => new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() }
/// });
/// </code>
/// </example>
public sealed class PooledHandlerContext
{
    internal PooledHandlerContext(
        string origin,
        Uri baseAddress,
        string profile,
        ConnectionPoolOptions options,
        ConnectionTransportOptions transport,
        CookieContainer? cookieContainer = null)
    {
        Origin = origin;
        BaseAddress = baseAddress;
        Profile = profile;
        Options = options;
        Transport = transport;
        CookieContainer = cookieContainer;
    }

    /// <summary>
    /// 该 Handler 对应的节点：默认档为 <c>https://api.example.com:443</c>，
    /// 命名传输档为 <c>https://api.example.com:443#档名</c>。
    /// </summary>
    public string Origin { get; }

    /// <summary>该 origin 的根地址（<c>https://api.example.com:443/</c>）。</summary>
    public Uri BaseAddress { get; }

    /// <summary>该节点所属的传输档名；默认档为空串。</summary>
    public string Profile { get; }

    /// <summary>建 Handler 时的池选项快照副本（改它不会影响池；要改配置请在构造池之前改）。</summary>
    public ConnectionPoolOptions Options { get; }

    /// <summary>
    /// 该节点的出口设置（默认档由池选项投影而来，命名档为 <see cref="ConnectionPoolOptions.AddTransport"/> 登记的那一份）。
    /// 工厂可以按它区分出口（例如按档选客户端证书）。
    /// </summary>
    public ConnectionTransportOptions Transport { get; }

    /// <summary>
    /// 该档该 origin 的 Cookie 容器（<see cref="ConnectionTransportOptions.UseCookies"/> 打开时非空）。
    /// 自行构造内层 Handler 时应挂到 <c>SocketsHttpHandler.CookieContainer</c> 上。
    /// </summary>
    public CookieContainer? CookieContainer { get; }

    /// <summary>按当前池选项与该档出口设置创建一个库默认的 <see cref="SocketsHttpHandler"/>（连接数、生命周期、TLS、Cookie 容器均已配好）。</summary>
    public SocketsHttpHandler CreateDefaultHandler() => DefaultHandlerFactory.Create(Options, Transport, CookieContainer);
}
