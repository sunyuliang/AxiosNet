namespace AxiosNet.Pooling;

/// <summary>
/// 连接池创建单个 origin 的 Handler 时传入的上下文，配合
/// <see cref="ConnectionPoolOptions.HandlerFactory"/> 使用。
/// <para>
/// 典型用法：在库默认的 <see cref="SocketsHttpHandler"/> 外面套自己的
/// <see cref="DelegatingHandler"/>（日志、签名、重试、埋点等），同时保留连接池的
/// 按 origin 隔离、连接复用与并发上限：
/// <code>
/// var pool = new HttpConnectionPool(new ConnectionPoolOptions
/// {
///     HandlerFactory = ctx => new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() }
/// });
/// </code>
/// </para>
/// </summary>
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
    /// 自定义 Handler 工厂若自行构造内层 Handler，应把它挂到 <c>SocketsHttpHandler.CookieContainer</c> 上，
    /// 否则同一池里的 Cookie 会被按 origin 切开。
    /// </summary>
    public CookieContainer? CookieContainer { get; }

    /// <summary>
    /// 按当前池选项创建一个库默认的 <see cref="SocketsHttpHandler"/>
    /// （连接数上限、连接生命周期、重定向、解压、代理、证书校验、共享 Cookie 容器等都已按选项配好）。
    /// 拿到后可以修改属性，或作为 <see cref="DelegatingHandler.InnerHandler"/> 被包装。
    /// </summary>
    public SocketsHttpHandler CreateDefaultHandler() => DefaultHandlerFactory.Create(Options, CookieContainer);
}
