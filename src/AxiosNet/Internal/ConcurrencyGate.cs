using AxiosNet.Telemetry;

namespace AxiosNet.Internal;

/// <summary>
/// 在途请求并发闸门（<see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/>）：每个 <see cref="AxiosClient"/> 一个。
/// 限速管字节速度，本闸门管同时在途的请求条数；排队可取消、可限深，超时抛 <c>ERR_QUEUE_TIMEOUT</c>，
/// 队列满抛 <c>ERR_QUEUE_FULL</c>。
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly SemaphoreSlim _slots;

    /// <summary>本闸门当前正在等槽位的请求数（只用于排队上限判定）。</summary>
    private int _queuedLocal;

    /// <summary>进程级排队深度：只在真的进等待时增减（快路径、「不排队」与被队列上限拒绝的那条路都不计）。</summary>
    private static long _queued;

    /// <summary>
    /// 当前正在等槽位的请求数（进程级，跨所有客户端 / 闸门）。
    /// </summary>
    internal static long QueueDepth => Volatile.Read(ref _queued);

    /// <summary>创建闸门并在此时固定上限；<paramref name="maxQueueLength"/> 为 <c>null</c> 表示不限排队深度。</summary>
    public ConcurrencyGate(int maxConcurrency, int? maxQueueLength = null)
    {
        MaxConcurrency = maxConcurrency;
        MaxQueueLength = maxQueueLength;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>闸门创建时固定的并发上限（之后不再变，改上限只能新建客户端）。</summary>
    public int MaxConcurrency { get; }

    /// <summary>闸门创建时固定的排队深度上限（<c>null</c> = 不限；之后不再变）。</summary>
    public int? MaxQueueLength { get; }

    /// <summary>
    /// 取一个并发槽位；返回的租约必须释放（<see cref="IDisposable.Dispose"/> 幂等，重复归还是安全的）。
    /// 排队期间被取消抛 <c>ERR_CANCELED</c>；等待上限 <c>queueTimeout</c> 为 <c>null</c> 一直等、<c>Zero</c> 不排队。
    /// </summary>
    /// <param name="config">本次请求的生效配置（错误消息带出去）。</param>
    /// <param name="request">触发本次排队的请求（错误消息带出去）。</param>
    /// <param name="cancellationToken">本次尝试的令牌，排队期间取消立刻收手。</param>
    /// <param name="queueTimeout">调用方给的排队上限（<c>QueueTimeout</c>）。</param>
    /// <param name="budgetRemaining">总预算剩余（<c>null</c> = 没设 <see cref="AxiosConfig.TotalTimeout"/>）；比排队上限更紧时由它封顶。</param>
    /// <param name="attempt">当前是第几次尝试，只用于「预算耗尽」的错误消息。</param>
    /// <remarks>非 async：稳态下槽位立刻可得，只有真的要排队时才转到 <see cref="AcquireQueuedAsync"/>。</remarks>
    public ValueTask<IDisposable> AcquireAsync(
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? queueTimeout,
        TimeSpan? budgetRemaining,
        int attempt)
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

        return AcquireQueuedAsync(config, request, cancellationToken, queueTimeout, budgetRemaining, attempt);
    }

    /// <summary>槽位已满的慢路径：显式「不排队」立刻失败，否则等排队上限、总预算或调用方令牌。</summary>
    private async ValueTask<IDisposable> AcquireQueuedAsync(
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? queueTimeout,
        TimeSpan? budgetRemaining,
        int attempt)
    {
        // 显式「不排队」：满了立刻失败，连计时器都不建（也不计入排队深度 —— 它没有真的等过）。
        if (queueTimeout == TimeSpan.Zero)
        {
            AxiosTelemetry.GateRejected(config, AxiosTelemetry.GateRejectTimeout);
            throw AxiosException.QueueTimeout(config, request, TimeSpan.Zero, MaxConcurrency);
        }

        // 总预算封顶排队等待：剩余预算不够时按预算报耗尽。
        // 放在排队深度计数之前：这里抛出的错误没有真的等过，不进水位。
        var cutByBudget = false;
        if (budgetRemaining is { } budget)
        {
            if (budget <= TimeSpan.Zero)
            {
                throw AxiosException.TotalTimeoutExceeded(config, request, attempt, config.TotalTimeout);
            }

            if (queueTimeout is not { } queued || budget < queued)
            {
                queueTimeout = budget;
                cutByBudget = true;
            }
        }

        // 排队容量：先占本地水位再判，保证「放行数 ≤ 上限」在并发下也成立（每个放行者都计入了同一个计数）。
        // 被拒的请求减回去，也不进进程级水位 —— 它没有真的等过。
        var depth = Interlocked.Increment(ref _queuedLocal);
        if (MaxQueueLength is int maxQueueLength && depth > maxQueueLength)
        {
            Interlocked.Decrement(ref _queuedLocal);
            AxiosTelemetry.GateRejected(config, AxiosTelemetry.GateRejectQueueFull);
            throw AxiosException.QueueFull(config, request, maxQueueLength, MaxConcurrency);
        }

        // 从这里开始是真的在等：排队深度 +1，无论等到、超时还是被取消都要减回去。
        Interlocked.Increment(ref _queued);
        try
        {
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
            catch (OperationCanceledException ex) when (cutByBudget)
            {
                // 真的是总预算到点了：报「预算耗尽」而不是「排队上限太小」——后者会把调用方引到错的旋钮上。
                throw AxiosException.TotalTimeoutExceeded(config, request, attempt, config.TotalTimeout, ex);
            }
            catch (OperationCanceledException ex)
            {
                AxiosTelemetry.GateRejected(config, AxiosTelemetry.GateRejectTimeout);
                throw AxiosException.QueueTimeout(config, request, limit, MaxConcurrency, ex);
            }

            return new SlotLease(_slots);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Decrement(ref _queuedLocal);
        }
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
