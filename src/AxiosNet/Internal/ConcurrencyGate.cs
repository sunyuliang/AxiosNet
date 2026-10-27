namespace AxiosNet.Internal;

/// <summary>
/// 在途请求并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）：每个 <see cref="AxiosClient"/> 一个。
/// 限速管字节速度，本闸门管同时在途的请求条数；排队可取消，超时抛 <c>ERR_QUEUE_TIMEOUT</c>。
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly SemaphoreSlim _slots;

    public ConcurrencyGate(int maxConcurrency)
    {
        MaxConcurrency = maxConcurrency;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>闸门创建时固定的并发上限（之后不再变，改上限只能新建客户端）。</summary>
    public int MaxConcurrency { get; }

    /// <summary>
    /// 取一个在途槽位；返回的租约必须释放（<see cref="IDisposable.Dispose"/> 幂等）。
    /// 排队期间被取消抛 <c>ERR_CANCELED</c>；等待上限 <c>queueTimeout</c> 为 <c>null</c> 一直等、<c>Zero</c> 不排队。
    /// </summary>
    /// <remarks>
    /// 本方法刻意<b>不是</b> <c>async</c>：稳态下槽位总是立刻可得，这条快路径每请求都要走一次，
    /// 进了状态机就白付一层异步机制（实测约 140 B/请求）。只有真的要排队时才落到
    /// <see cref="AcquireQueuedAsync"/> 的异步路径。
    /// </remarks>
    public ValueTask<IDisposable> AcquireAsync(
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? queueTimeout)
    {
        // 已取消的令牌不必先抢槽位，直接翻译成统一错误模型。
        if (cancellationToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(config, request, "请求在等待并发槽位前已被取消");
        }

        // 快路径：非阻塞尝试（Wait(0) 只做一次原子计数）。
        if (_slots.Wait(0))
        {
            return new ValueTask<IDisposable>(new SlotLease(_slots));
        }

        return AcquireQueuedAsync(config, request, cancellationToken, queueTimeout);
    }

    /// <summary>槽位已满的慢路径：显式「不排队」立刻失败，否则等排队上限或调用方令牌。</summary>
    private async ValueTask<IDisposable> AcquireQueuedAsync(
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? queueTimeout)
    {
        // 显式「不排队」：满了立刻失败，连计时器都不建。
        if (queueTimeout == TimeSpan.Zero)
        {
            throw AxiosException.QueueTimeout(config, request, TimeSpan.Zero, MaxConcurrency);
        }

        if (queueTimeout is not { } limit)
        {
            try
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                throw AxiosException.Canceled(config, request, "请求在等待并发槽位时被取消", ex);
            }

            return new SlotLease(_slots);
        }

        // 队列超时用独立令牌源：不污染调用方令牌，排队时间也不计入 Timeout。
        using var queueCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        queueCts.CancelAfter(Timeouts.Clamp(limit));

        try
        {
            await _slots.WaitAsync(queueCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // 两者同时触发时以「调用方取消」为准。
            throw AxiosException.Canceled(config, request, "请求在等待并发槽位时被取消", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw AxiosException.QueueTimeout(config, request, limit, MaxConcurrency, ex);
        }

        return new SlotLease(_slots);
    }

    /// <summary>
    /// 槽位租约：释放幂等。用 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 换出引用，释放与归还计数同一步完成。
    /// </summary>
    private sealed class SlotLease : IDisposable
    {
        private SemaphoreSlim? _slots;

        public SlotLease(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
