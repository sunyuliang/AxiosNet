using AxiosNet.Pooling;

namespace AxiosNet.Internal.Response;

/// <summary>流式响应的正文包装：把本次尝试的剩余预算延续到正文读取阶段，并统一正文阶段的取消 / IO 异常。</summary>
/// <remarks>超时 → <c>ECONNABORTED</c>，其余取消 → <c>ERR_CANCELED</c>，原始异常留在 <c>Cause</c>；既无预算又无外部令牌时不建 CTS。</remarks>
internal sealed class TimeoutStream : DelegatingStream
{
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage _request;
    private readonly CancellationToken _externalToken;

    /// <summary>超时计时源；无剩余预算时为 <c>null</c>。</summary>
    private readonly CancellationTokenSource? _timeoutCts;

    /// <summary>「外部令牌 + 计时器」的链接源；两者都成立时才创建。</summary>
    private readonly CancellationTokenSource? _linkedCts;

    /// <summary>交给底层的令牌（预计算）。</summary>
    private readonly CancellationToken _token;

    /// <summary>构造时预算已耗尽：核心方法入口直接抛 <c>ECONNABORTED</c>。</summary>
    private readonly bool _budgetExhausted;

    /// <summary>调用方自备令牌对应的链接源缓存。</summary>
    private CancellationToken _cachedCallerToken;

    private CancellationTokenSource? _cachedCallerCts;

    /// <summary>包装流式正文：<paramref name="remaining"/> 为剩余超时预算（<c>null</c> 不超时；≤ 0 视为已耗尽）。</summary>
    public TimeoutStream(
        Stream inner,
        TimeSpan? remaining,
        CancellationToken externalToken,
        AxiosConfig config,
        HttpRequestMessage request,
        TimeSpan? effectiveTimeout = null)
        : base(inner)
    {
        _config = config;
        _request = request;
        _externalToken = externalToken;
        _effectiveTimeout = effectiveTimeout;

        // 预算一次性，中途不重置；只有需要计时才建计时源。
        var budget = default(TimeSpan);
        var timed = false;
        if (remaining is TimeSpan value)
        {
            if (value > TimeSpan.Zero)
            {
                budget = value;
                timed = true;
            }
            else
            {
                _budgetExhausted = true;
            }
        }

        var outsideCancelable = externalToken.CanBeCanceled;

        if (!timed)
        {
            // 不计时：可取消就直接用外部令牌，否则用 None。
            _token = outsideCancelable ? externalToken : CancellationToken.None;
            return;
        }

        _timeoutCts = new CancellationTokenSource();
        _timeoutCts.CancelAfter(Timeouts.Clamp(budget));

        if (outsideCancelable)
        {
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _timeoutCts.Token);
            _token = _linkedCts.Token;
        }
        else
        {
            // 只有计时器：直接用计时源的令牌。
            _token = _timeoutCts.Token;
        }
    }

    /// <summary>被总预算压小后的真实上限，只用于错误消息。</summary>
    private readonly TimeSpan? _effectiveTimeout;

    /// <summary>读一块：内层同步完成时原样透传，异步或故障才走异步路径。</summary>
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfReleased();
        ThrowIfBudgetExhausted(callerToken);

        var token = Link(callerToken);

        ValueTask<int> pending;
        try
        {
            pending = Inner.ReadAsync(buffer, token);

            if (pending.IsCompletedSuccessfully)
            {
                return pending;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidDataException)
        {
            throw Translate(ex, callerToken);
        }

        return FinishReadAsync(pending, callerToken);
    }

    /// <summary>内层未同步完成时的读路径：翻译取消 / 超时 / IO 失败。</summary>
    private async ValueTask<int> FinishReadAsync(ValueTask<int> pending, CancellationToken callerToken)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidDataException)
        {
            // 读取中断归一到同一错误模型。
            throw Translate(ex, callerToken);
        }
    }

    /// <summary>写：查预算后把调用方令牌并进预算令牌再写。</summary>
    protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfReleased();
        ThrowIfBudgetExhausted(callerToken);

        var token = Link(callerToken);
        try
        {
            await Inner.WriteAsync(buffer, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidDataException)
        {
            throw Translate(ex, callerToken);
        }
    }

    /// <summary>刷新：查预算后同样翻译取消 / IO 失败。</summary>
    protected override async ValueTask FlushCoreAsync(CancellationToken callerToken)
    {
        ThrowIfReleased();
        ThrowIfBudgetExhausted(callerToken);

        var token = Link(callerToken);
        try
        {
            await Inner.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or InvalidDataException)
        {
            throw Translate(ex, callerToken);
        }
    }

    /// <summary>把本次调用的令牌链接到「外部取消 + 超时」；反复传同一个自备令牌时按缓存复用，换令牌才释放上一个。</summary>
    private CancellationToken Link(CancellationToken callerToken)
    {
        var linked = _token;
        if (!callerToken.CanBeCanceled || callerToken == linked || callerToken == _externalToken)
        {
            return linked;
        }

        // 本层没有取消来源：调用方令牌即唯一来源。
        if (!linked.CanBeCanceled)
        {
            return callerToken;
        }

        if (callerToken == _cachedCallerToken && _cachedCallerCts is not null)
        {
            return _cachedCallerCts.Token;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, linked);
        var previous = _cachedCallerCts;
        _cachedCallerToken = callerToken;
        _cachedCallerCts = cts;

        // 释放被替换的旧链接源。
        previous?.Dispose();
        return cts.Token;
    }

    /// <summary>本包装是否已释放；释放后读 / 写 / Flush 一律按「流已关闭」失败。</summary>
    private bool _released;

    /// <summary>核心方法的第一步：已释放就按 <see cref="Stream"/> 约定失败。</summary>
    /// <remarks>
    /// <see cref="OnReleased"/> 会释放全部链接源与计时源，之后取 <see cref="CancellationTokenSource.Token"/>
    /// 只会抛出内部实现细节。预算判定也要排在它后面，否则预算耗尽的流会把「已关闭」报成超时。
    /// </remarks>
    /// <exception cref="ObjectDisposedException">包装流已释放。</exception>
    private void ThrowIfReleased()
    {
        if (Volatile.Read(ref _released))
        {
            throw new ObjectDisposedException(null, "响应流已释放：流式正文只能读一次，释放后不能再读 / 写 / Flush。");
        }
    }

    /// <summary>预算在构造时即耗尽：按 <c>Caller → Canceled → Timeout</c> 判定归属，确定性失败。</summary>
    private void ThrowIfBudgetExhausted(CancellationToken callerToken)
    {
        if (!_budgetExhausted)
        {
            return;
        }

        switch (CancellationClassifier.Classify(_externalToken, _timeoutCts?.Token, callerToken))
        {
            case CancellationKind.Caller:
                // 调用方令牌已取消，最终仍是 ERR_CANCELED。
                throw AxiosException.Canceled(_config, _request, new OperationCanceledException(callerToken));

            case CancellationKind.Canceled:
                throw AxiosException.Canceled(_config, _request);

            default:
                throw AxiosException.Timeout(_config, _request, null, _effectiveTimeout);
        }
    }

    /// <summary>把内层流的失败翻译成库的错误码：超时 → <c>ECONNABORTED</c>，取消 → <c>ERR_CANCELED</c>，解压失败 → <c>ERR_BAD_RESPONSE</c>，IO / 传输失败 → <c>ERR_NETWORK</c>。</summary>
    private Exception Translate(Exception failure, CancellationToken callerToken)
    {
        if (failure is InvalidDataException decompression)
        {
            // 声明的 Content-Encoding 与正文不符：不是传输失败，重发也还是这份坏正文。
            return AxiosException.DecompressionFailed(_config, _request, decompression);
        }

        if (failure is not OperationCanceledException canceled)
        {
            return AxiosException.Network(_config, _request, failure);
        }

        return CancellationClassifier.Classify(_externalToken, _timeoutCts?.Token, callerToken) == CancellationKind.Timeout
            ? AxiosException.Timeout(_config, _request, canceled, _effectiveTimeout)
            : AxiosException.Canceled(_config, _request, canceled);
    }

    /// <summary>释放链接源与超时计时器；内层流由基类释放。</summary>
    /// <remarks>
    /// 先置释放位：释放返回之后的读 / 写 / Flush 直接按「流已关闭」失败，不会再去取已释放的令牌。
    /// 基类先关内层流再回调这里，与释放并发的读由内层流自身的释放状态决定。
    /// </remarks>
    protected override void OnReleased()
    {
        Volatile.Write(ref _released, true);

        _cachedCallerCts?.Dispose();
        _linkedCts?.Dispose();
        _timeoutCts?.Dispose();
    }
}

/// <summary>流式响应的所有权包装：连接池租约、<see cref="HttpResponseMessage"/> 与跨终端租约随响应流 Dispose 一并释放。</summary>
/// <remarks>
/// 持有资源不可替代：租约与并发槽位只在 <see cref="OnReleased"/> 归还。调用方忘记释放时对象可被回收，
/// 但计数不会自己归零（端点因此永久不可驱逐），所以终结器只做一次兜底归还。
/// </remarks>
internal sealed class ResponseStreamOwner : DelegatingStream
{
    private readonly HttpResponseMessage? _message;
    private readonly PoolLease? _lease;
    private readonly IDisposable? _leases;

    public ResponseStreamOwner(Stream inner, HttpResponseMessage? message, PoolLease? lease, IDisposable? leases = null)
        : base(inner)
    {
        _message = message;
        _lease = lease;
        _leases = leases;
    }


    /// <summary>兜底：调用方没释放就被回收时归还租约与并发槽位（两者都幂等，与正常释放竞争只生效一次）。</summary>
    ~ResponseStreamOwner() => ReleaseOwnership();

    /// <summary>释放响应消息、连接租约与跨终端租约；前一个抛异常也继续释放其余。内层流由基类释放。</summary>
    protected override void OnReleased()
    {
        // 已走正常释放路径，终结器不必再跑。
        GC.SuppressFinalize(this);
        ReleaseOwnership();
    }

    /// <summary>归还三项所有权；每项独立兜住异常，任一项失败不影响其余。</summary>
    private void ReleaseOwnership()
    {
        try
        {
            _message?.Dispose();
        }
        catch
        {
            // 兜底路径不把释放异常抛给终结器线程。
        }

        try
        {
            _lease?.Dispose();
        }
        catch
        {
            // 租约归还是幂等计数，失败也不该阻断并发槽位。
        }

        try
        {
            _leases?.Dispose();
        }
        catch
        {
            // 同上。
        }
    }
}
