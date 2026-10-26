namespace AxiosNet.Internal;

/// <summary>
/// 在途请求并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）：每个
/// <see cref="AxiosClient"/> 一个，按实例共享。
/// <para>
/// 与限速（<see cref="AxiosConfig.LimitSettings.MaxRate"/>）分工明确：限速管<b>字节速度</b>，
/// 本闸门管<b>同时在途的请求条数</b>。小请求高频打服务端时，限速拦不住（每条请求都只有几 KB），
/// 压垮对端的是「同时开了几百条连接」，只有并发上限能兜住。
/// </para>
/// <para>
/// 排队语义：槽位占满时等待可取消（<see cref="AxiosConfig.CancellationToken"/>），
/// 也可用 <see cref="AxiosConfig.LimitSettings.QueueTimeout"/> 给等待设上限 ——
/// 超时抛 <c>ERR_QUEUE_TIMEOUT</c>，与请求超时（<c>ECONNABORTED</c>）区分开：
/// 前者请求还没发出去，后者已经发出去在等回包。
/// </para>
/// <para>
/// 不实现 <see cref="IDisposable"/>：<see cref="SemaphoreSlim"/> 只有在访问过
/// <c>AvailableWaitHandle</c> 时才需要释放（本类不碰它）。反过来「释放」会引入一类真实故障 ——
/// 流式响应的槽位由调用方持有的正文流占着，<see cref="AxiosClient.Dispose"/> 之后才释放，
/// 那时信号量已 Dispose 就会让 <c>Release</c> 抛 <see cref="ObjectDisposedException"/>。
/// </para>
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
    /// <para>
    /// 快路径（有空位）不建 CTS、不注册计时器：绝大多数请求走这一条，
    /// 只有真的需要排队时才会分配计时器与状态机。
    /// </para>
    /// </summary>
    /// <param name="config">生效配置（错误对象要带它）。</param>
    /// <param name="request">本次尝试的请求对象（尚未发出，只用于错误上下文）。</param>
    /// <param name="cancellationToken">调用方令牌：排队期间被取消抛 <c>ERR_CANCELED</c>。</param>
    /// <param name="queueTimeout">等待上限：<c>null</c> 一直等 / <c>Zero</c> 不排队 / 大于零按超时失败。</param>
    public async ValueTask<IDisposable> AcquireAsync(
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? queueTimeout)
    {
        // 已经取消的令牌不必先抢槽位：抢到之后还要立刻归还，徒增一次释放。
        // 翻译成统一错误模型（裸 OperationCanceledException 不在这条管线的契约里）。
        if (cancellationToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(config, request, "请求在等待并发槽位前已被取消");
        }

        // 快路径：非阻塞尝试。Wait(0) 不阻塞线程，只是做一次原子计数。
        if (_slots.Wait(0))
        {
            return new SlotLease(_slots);
        }

        // 显式「不排队」：满了立刻失败，连计时器都不建（Zero 无法交给 CancelAfter 表达「立即」，
        // 而 WaitAsync(0) 的成功路径已被上面吃掉，走到这里就一定是失败）。
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

        // 队列超时用独立令牌源：它不能污染调用方令牌，也不能被 Timeout 的计时器牵连 ——
        // 排队时间不计入 Timeout（见 MaxConcurrentRequests 的文档），两者必须各管各的。
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
            // 两者同时触发时以「调用方取消」为准：那才是调用方真正做的事，
            // 报成队列超时会让它以为限流配置有问题。
            throw AxiosException.Canceled(config, request, "请求在等待并发槽位时被取消", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw AxiosException.QueueTimeout(config, request, limit, MaxConcurrency, ex);
        }

        return new SlotLease(_slots);
    }

    /// <summary>
    /// 槽位租约：释放幂等（重复 Dispose 不会把信号量的计数还多）。
    /// <para>
    /// 用 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 换出引用而不是布尔标记：
    /// 释放与归还计数在同一步完成，不存在「标记已置位、Release 还没跑」的窗口。
    /// </para>
    /// </summary>
    private sealed class SlotLease : IDisposable
    {
        private SemaphoreSlim? _slots;

        public SlotLease(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
