using System.Diagnostics;
using AxiosNet.Telemetry;

namespace AxiosNet.Pooling;

/// <summary>
/// 单个 origin（scheme://host:port）对应的池化节点：持有一个 Handler
/// （默认 <see cref="SocketsHttpHandler"/>，也可由 <see cref="ConnectionPoolOptions.HandlerFactory"/> 提供）与其上的 HttpClient。
/// </summary>
internal sealed class PooledEndpoint : IDisposable
{
    private readonly HttpConnectionPool _owner;

    /// <summary>池 key（scheme / host / port）。</summary>
    public OriginKey Key { get; }

    /// <summary>origin 文本（<c>scheme://host:port</c>），也用于 HandlerFactory 上下文与统计展示。</summary>
    public string Origin { get; }

    /// <summary>该 origin 上的 Handler（默认 <see cref="SocketsHttpHandler"/>，或工厂返回的实例）；由本节点释放。</summary>
    public HttpMessageHandler Handler { get; }

    /// <summary>发往该 origin 的请求共用这一个 HttpClient（超时交给请求级控制）。</summary>
    public HttpClient Client { get; }

    /// <summary>当前未归还的租约数（只在 <see cref="SyncRoot"/> 锁内读写）。</summary>
    public int ActiveLeases { get; private set; }

    /// <summary>最近一次租用 / 归还的时刻（由单调时钟换算，供统计展示；驱逐判定只看 <see cref="LastUsedTimestamp"/>）。</summary>
    public DateTime LastUsedUtc => DateTime.UtcNow - IdleSinceLastUse(Stopwatch.GetTimestamp());

    /// <summary>最近一次租用 / 归还的单调时间戳：驱逐判定与 LRU 排序的时间源，不受系统时钟调整影响。</summary>
    internal long LastUsedTimestamp => Volatile.Read(ref _lastUsedTimestamp);

    private long _lastUsedTimestamp = Stopwatch.GetTimestamp();

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 节点已被池提前摘除（池 Dispose 或 <see cref="HttpConnectionPool.ClearCookies"/>）：最后一条租约归还时自行销毁。
    /// 标记在锁内、锁外都会写，读发生在 <see cref="Release"/> 的锁内。
    /// </summary>
    public bool DisposeWhenIdle
    {
        get => _disposeWhenIdle;
        set => _disposeWhenIdle = value;
    }

    private volatile bool _disposeWhenIdle;

    /// <summary>租约计数与释放共用的锁。</summary>
    public object SyncRoot { get; } = new();

    public PooledEndpoint(
        HttpConnectionPool owner,
        OriginKey key,
        ConnectionPoolOptions options,
        CookieContainer? cookieContainer)
    {
        _owner = owner;
        Key = key;
        Origin = key.ToString();
        Handler = CreateHandler(Origin, options, cookieContainer);
        Client = new HttpClient(Handler, disposeHandler: false)
        {
            // 超时由 AxiosClient 按请求统一控制。
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

        // 给工厂一份选项快照。
        var context = new PooledHandlerContext(origin, new Uri(origin + "/"), options.Clone(), cookieContainer);
        var handler = options.HandlerFactory(context);

        if (handler is null)
        {
            throw new InvalidOperationException($"ConnectionPoolOptions.HandlerFactory 对 origin '{origin}' 返回了 null，必须返回一个可用的 HttpMessageHandler。");
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

            // 与 LastUsedTimestamp 的 Volatile.Read 配对：32 位平台上 64 位写入不加 Volatile 可能被读到半截。
            Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());

            // 进程级活跃租约水位（ObservableGauge 收集时读）。
            AxiosTelemetry.EnterLease();
            return true;
        }
    }

    public void Release()
    {
        var retire = false;
        var released = false;
        lock (SyncRoot)
        {
            if (ActiveLeases > 0)
            {
                ActiveLeases--;
                released = true;
            }

            Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());

            if (DisposeWhenIdle && ActiveLeases == 0 && !IsDisposed)
            {
                IsDisposed = true;
                retire = true;
            }
        }

        // 只有真的减掉了才减水位，避免重复 Release 让计数变成负数。
        if (released)
        {
            AxiosTelemetry.ExitLease();
        }

        if (retire)
        {
            // 节点已被池摘除（池关闭 / ClearCookies）：归还租约的同时把自己从池里摘掉并释放。
            _owner.Forget(this);
            DisposeClientAndHandler();
        }
    }

    /// <summary>自最近一次使用起经过的时长（单调时钟；负值按零处理）。</summary>
    internal TimeSpan IdleSinceLastUse(long now)
    {
        var seconds = (double)(now - LastUsedTimestamp) / Stopwatch.Frequency;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>立即释放：标记已释放并关闭 Client / Handler，不等在途租约（在途请求会失败）。</summary>
    public void Dispose()
    {
        if (MarkDisposed())
        {
            DisposeClientAndHandler();
        }
    }

    /// <summary>只置「已释放」标记并返回是否首次置位（供池的锁内判定、锁外释放路径使用）。</summary>
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
            // Handler 以 disposeHandler:false 传入，不会被连带释放。
            Client.Dispose();
        }
        catch
        {
            // 关闭阶段的异常不影响池的清理。
        }

        try
        {
            Handler.Dispose();
        }
        catch
        {
            // 连接关闭异常不影响池关闭。
        }
    }
}
