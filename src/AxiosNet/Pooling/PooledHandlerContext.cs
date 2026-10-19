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
    internal PooledHandlerContext(string origin, Uri baseAddress, ConnectionPoolOptions options)
    {
        Origin = origin;
        BaseAddress = baseAddress;
        Options = options;
    }

    /// <summary>该 Handler 对应的 origin，形如 <c>https://api.example.com:443</c>。</summary>
    public string Origin { get; }

    /// <summary>该 origin 的根地址（<c>https://api.example.com:443/</c>）。</summary>
    public Uri BaseAddress { get; }

    /// <summary>本次连接池的选项（只读使用；要改配置请在构造池之前改）。</summary>
    public ConnectionPoolOptions Options { get; }

    /// <summary>
    /// 按当前池选项创建一个库默认的 <see cref="SocketsHttpHandler"/>
    /// （连接数上限、连接生命周期、重定向、解压、代理、证书校验等都已按选项配好）。
    /// 拿到后可以修改属性，或作为 <see cref="DelegatingHandler.InnerHandler"/> 被包装。
    /// </summary>
    public SocketsHttpHandler CreateDefaultHandler() => DefaultHandlerFactory.Create(Options);
}
