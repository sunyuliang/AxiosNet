namespace AxiosNet;

// AxiosClient 的释放与在途请求计数。
public sealed partial class AxiosClient
{
    private const string DisposedMessage = "AxiosClient 已释放，不能发起新请求";

    /// <summary>最后一个在途请求结束时，补做 <see cref="Dispose"/> 推迟的资源释放。</summary>
    private void ReleaseInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) == 1)
        {
            DisposeResources();
        }
    }

    /// <summary>阶段 0 —— 入口守卫：先占一个在途名额，再复核释放标记；必须在调用方的 <c>try</c> 之外（它抛时名额还没占上，不该归还）。</summary>
    private void EnterInFlight(AxiosConfig config)
    {
        // ObjectDisposedException.ThrowIf 是 .NET 7 才有的 API，手写以兼容 net6.0 目标。
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw DisposedError(config);
        }

        Interlocked.Increment(ref _inFlight);

        if (Volatile.Read(ref _disposed) == 1)
        {
            ReleaseInFlight();
            throw DisposedError(config);
        }
    }

    /// <summary>已释放实例上的请求统一报 <c>ERR_CANCELED</c>，不漏裸 <see cref="ObjectDisposedException"/>。</summary>
    private static AxiosException DisposedError(AxiosConfig config)
        => AxiosException.Canceled(config, null, DisposedMessage, new ObjectDisposedException(nameof(AxiosClient)));

    /// <summary>释放实例：不打断在途请求，资源释放推迟到最后一个请求结束；此后新请求抛 <c>ERR_CANCELED</c>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _inFlight) > 0)
        {
            // 在途请求结束时由 ReleaseInFlight 补做释放
            return;
        }

        DisposeResources();
    }

    /// <summary>资源释放本体（幂等）：注入的 HttpClient/Handler 与本实例自建的池。</summary>
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _customClient?.Dispose();

        if (_ownsCustomHandler)
        {
            _customHandler?.Dispose();
        }

        if (_ownsPool)
        {
            // 优雅关闭：空闲节点立即释放，在途请求的最后一条租约归还时才回收节点。
            _pool?.Dispose();
        }
    }

    /// <summary>与 <see cref="Dispose"/> 等价；连接池关闭本身是同步的，实现它只为支持 <c>await using</c>。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
