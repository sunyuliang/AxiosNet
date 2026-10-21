namespace AxiosNet.Pooling;

/// <summary>
/// 单个 origin（scheme://host:port）对应的池化节点：持有一个调优过的 Handler
/// （默认 <see cref="SocketsHttpHandler"/>，也可由 <see cref="ConnectionPoolOptions.HandlerFactory"/> 提供）
/// 与其上的 HttpClient。
/// </summary>
internal sealed class PooledEndpoint : IDisposable
{
    private readonly HttpConnectionPool _owner;

    public string Origin { get; }

    public HttpMessageHandler Handler { get; }

    public HttpClient Client { get; }

    public int ActiveLeases { get; private set; }

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 池已 Dispose：本节点在最后一条租约归还时自行销毁
    /// —— 直接 Dispose 会让「关闭时还有请求在途」的调用方拿到 ObjectDisposedException。
    /// </summary>
    public bool DisposeWhenIdle { get; set; }

    /// <summary>租约计数与释放共用的锁。</summary>
    public object SyncRoot { get; } = new();

    public PooledEndpoint(
        HttpConnectionPool owner,
        string origin,
        ConnectionPoolOptions options,
        CookieContainer? cookieContainer)
    {
        _owner = owner;
        Origin = origin;
        Handler = CreateHandler(origin, options, cookieContainer);
        Client = new HttpClient(Handler, disposeHandler: false)
        {
            // 超时由 AxiosClient 按请求统一控制，避免 Handler 级超时干扰
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpMessageHandler CreateHandler(
        string origin,
        ConnectionPoolOptions options,
        CookieContainer? cookieContainer)
    {
        if (options.HandlerFactory is null)
        {
            return DefaultHandlerFactory.Create(options, cookieContainer);
        }

        var context = new PooledHandlerContext(origin, new Uri(origin + "/"), options, cookieContainer);
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
        var retire = false;
        lock (SyncRoot)
        {
            if (ActiveLeases > 0)
            {
                ActiveLeases--;
            }

            LastUsedUtc = DateTime.UtcNow;

            if (DisposeWhenIdle && ActiveLeases == 0 && !IsDisposed)
            {
                IsDisposed = true;
                retire = true;
            }
        }

        if (retire)
        {
            // 池已关闭：归还租约的同时把自己从池里摘掉并释放，避免「关闭后永久挂着一个 Handler」。
            _owner.Forget(this);
            DisposeClientAndHandler();
        }
    }

    public void Dispose()
    {
        if (MarkDisposed())
        {
            DisposeClientAndHandler();
        }
    }

    /// <summary>
    /// 只置「已释放」标记并返回是否首次置位。
    /// <para>
    /// 供池的「锁内判定、锁外释放」驱逐路径使用：关闭 Handler 会断开该 origin 的全部连接、可能耗时，
    /// 不能占着 <see cref="SyncRoot"/> 让同 origin 的租用 / 统计 / 另一次驱逐排队。
    /// </para>
    /// </summary>
    public bool MarkDisposed()
    {
        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return false;
            }

            IsDisposed = true;
            return true;
        }
    }

    /// <summary>释放 HttpClient 与 Handler（调用方须已通过 <see cref="MarkDisposed"/> 置位）。</summary>
    internal void ReleaseResources() => DisposeClientAndHandler();

    private void DisposeClientAndHandler()
    {
        try
        {
            // HttpClient 自身也持有计时器等资源（Handler 以 disposeHandler:false 传入，不会被连带释放）
            Client.Dispose();
        }
        catch
        {
            // 忽略：关闭阶段的异常不应影响池的清理
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
