namespace AxiosNet.Pooling;

/// <summary>连接池创建单个 origin 的 Handler 时传入的上下文，配合 <see cref="ConnectionPoolOptions.HandlerFactory"/> 使用。</summary>
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
        ConnectionPoolOptions options,
        CookieContainer? cookieContainer = null)
    {
        Origin = origin;
        BaseAddress = baseAddress;
        Options = options;
        CookieContainer = cookieContainer;
    }

    /// <summary>该 Handler 对应的 origin，形如 <c>https://api.example.com:443</c>。</summary>
    public string Origin { get; }

    /// <summary>该 origin 的根地址（<c>https://api.example.com:443/</c>）。</summary>
    public Uri BaseAddress { get; }

    /// <summary>本次连接池的选项（只读使用；要改配置请在构造池之前改）。</summary>
    public ConnectionPoolOptions Options { get; }

    /// <summary>
    /// 池共享的 Cookie 容器（<see cref="ConnectionPoolOptions.UseCookies"/> 打开时非空）。
    /// 自行构造内层 Handler 时应挂到 <c>SocketsHttpHandler.CookieContainer</c> 上。
    /// </summary>
    public CookieContainer? CookieContainer { get; }

    /// <summary>按当前池选项创建一个库默认的 <see cref="SocketsHttpHandler"/>（连接数、生命周期、TLS、Cookie 容器均已配好）。</summary>
    public SocketsHttpHandler CreateDefaultHandler() => DefaultHandlerFactory.Create(Options, CookieContainer);
}
