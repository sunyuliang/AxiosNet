namespace AxiosNet.Pooling;

/// <summary>
/// 单个 origin（scheme://host:port）对应的池化节点：
/// 持有一个调优过的 HttpMessageHandler（默认 <see cref="SocketsHttpHandler"/>，
/// 也可由 <see cref="ConnectionPoolOptions.HandlerFactory"/> 提供/包装）与其上的 HttpClient，
/// Handler 内部负责 TCP 连接复用、排队与连接数上限。
/// </summary>
internal sealed class PooledEndpoint : IDisposable
{
    public string Origin { get; }

    public HttpMessageHandler Handler { get; }

    public HttpClient Client { get; }

    public int ActiveLeases { get; private set; }

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public bool IsDisposed { get; private set; }

    /// <summary>租约计数与释放共用的锁。</summary>
    public object SyncRoot { get; } = new();

    public PooledEndpoint(string origin, ConnectionPoolOptions options)
    {
        Origin = origin;
        Handler = CreateHandler(origin, options);
        Client = new HttpClient(Handler, disposeHandler: false)
        {
            // 超时由 AxiosClient 按请求统一控制，避免 Handler 级超时干扰
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpMessageHandler CreateHandler(string origin, ConnectionPoolOptions options)
    {
        if (options.HandlerFactory is null)
        {
            return DefaultHandlerFactory.Create(options);
        }

        var context = new PooledHandlerContext(origin, new Uri(origin + "/"), options);
        var handler = options.HandlerFactory(context);

        if (handler is null)
        {
            throw new InvalidOperationException(
                $"ConnectionPoolOptions.HandlerFactory 对 origin '{origin}' 返回了 null，必须返回一个可用的 HttpMessageHandler。");
        }

        return handler;
    }

    /// <summary>在 SyncRoot 下调用；返回 false 表示节点已被驱逐。</summary>
    public bool TryAcquire()
    {
        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return false;
            }

            ActiveLeases++;
            LastUsedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void Release()
    {
        lock (SyncRoot)
        {
            if (ActiveLeases > 0)
            {
                ActiveLeases--;
            }

            LastUsedUtc = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
        }

        try
        {
            Handler.Dispose();
        }
        catch
        {
            // 连接关闭异常不影响池关闭
        }
    }
}
