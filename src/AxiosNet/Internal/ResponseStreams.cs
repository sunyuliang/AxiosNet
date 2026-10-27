using AxiosNet.Pooling;

namespace AxiosNet.Internal;

/// <summary>
/// 流式响应的正文包装：把请求总超时延续到正文读取阶段，并统一正文阶段的取消 / IO 异常。
/// 用剩余预算建 CTS 并与外部取消链接：超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>，
/// 调用方自己的令牌取消则原样抛出。无条件包装（<see cref="AxiosConfig.Timeout"/> 为 0 时预算传 <c>null</c>），
/// 令牌交给底层那一次 I/O；同步入口实现为对异步核心的阻塞等待。
/// </summary>
internal sealed class TimeoutStream : DelegatingStream
{
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage _request;
    private readonly CancellationToken _externalToken;
    private readonly CancellationTokenSource _timeoutCts;
    private readonly CancellationTokenSource _linkedCts;

    /// <summary>构造时预算已耗尽：核心方法入口直接抛 <c>ECONNABORTED</c>，不与已到达的字节赛跑。</summary>
    private readonly bool _budgetExhausted;

    /// <summary>最近一个调用方自备令牌对应的链接源缓存，避免每块都新建 CTS。</summary>
    private CancellationToken _cachedCallerToken;

    private CancellationTokenSource? _cachedCallerCts;

    /// <summary>
    /// 包装流式正文。<paramref name="remaining"/> 为剩余超时预算，<c>null</c> 表示不超时；
    /// <paramref name="idleTimeout"/> 大于零时改为空闲超时（只在实际读写期间计时）。
    /// </summary>
    public TimeoutStream(
        Stream inner,
        TimeSpan? remaining,
        CancellationToken externalToken,
        AxiosConfig config,
        HttpRequestMessage request,
        TimeSpan? idleTimeout = null)
        : base(inner)
    {
        _config = config;
        _request = request;
        _externalToken = externalToken;

        _timeoutCts = new CancellationTokenSource();

        // 空闲超时优先；计时器不在构造时启动，只在真实读写期间进行。
        if (idleTimeout is { } idle && idle > TimeSpan.Zero)
        {
            _idleTimeout = idle;
        }
        else if (remaining is TimeSpan budget)
        {
            if (budget > TimeSpan.Zero)
            {
                _timeoutCts.CancelAfter(Timeouts.Clamp(budget));
            }
            else
            {
                _budgetExhausted = true;
            }
        }

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _timeoutCts.Token);
    }

    /// <summary>每次成功读到数据后重置的空闲计时；<see cref="TimeSpan.Zero"/> 表示未启用。</summary>
    private readonly TimeSpan _idleTimeout;

    /// <summary>
    /// 读一块。内层同步完成（缓冲命中的常见情形）时整条链路零分配：不建状态机、结果原样透传；
    /// 真异步或故障才落到异步路径。同步抛出的异常同样要翻译（否则空闲计时不会撤销）。
    /// </summary>
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var token = Link(callerToken);
        ArmIdleTimer();

        ValueTask<int> pending;
        try
        {
            pending = Inner.ReadAsync(buffer, token);

            if (pending.IsCompletedSuccessfully)
            {
                DisarmIdleTimer();
                return pending;
            }
        }
        catch (OperationCanceledException ex)
        {
            DisarmIdleTimer();
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            DisarmIdleTimer();
            throw AxiosException.Network(_config, _request, ex);
        }

        return FinishReadAsync(pending, callerToken);
    }

    /// <summary>内层未同步完成时的读路径：翻译取消 / 超时 / IO 失败，并撤销空闲计时。</summary>
    private async ValueTask<int> FinishReadAsync(ValueTask<int> pending, CancellationToken callerToken)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // 正文读到一半连接断了：与非流式路径同一错误模型。
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            DisarmIdleTimer();
        }
    }

    /// <summary>空闲计时只在一次真实 I/O 期间进行：开始读写时启动，完成（含异常）时撤销。</summary>
    private void ArmIdleTimer()
    {
        if (_idleTimeout > TimeSpan.Zero && !_timeoutCts.IsCancellationRequested)
        {
            _timeoutCts.CancelAfter(Timeouts.Clamp(_idleTimeout));
        }
    }

    private void DisarmIdleTimer()
    {
        if (_idleTimeout > TimeSpan.Zero && !_timeoutCts.IsCancellationRequested)
        {
            _timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var token = Link(callerToken);
        ArmIdleTimer();
        try
        {
            await Inner.WriteAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            DisarmIdleTimer();
        }
    }

    protected override async ValueTask FlushCoreAsync(CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var token = Link(callerToken);
        ArmIdleTimer();
        try
        {
            await Inner.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            DisarmIdleTimer();
        }
    }

    /// <summary>
    /// 把本次调用的令牌链接到「外部取消 + 超时」上：无令牌或已知令牌直接复用预建的链接源，
    /// 反复传同一个自备令牌时按缓存复用。换令牌才释放上一个（单读者顺序读保证其读写已返回）。
    /// 上一代链接源由本方法自己回收，调用方不持有任何要释放的 CTS。
    /// </summary>
    private CancellationToken Link(CancellationToken callerToken)
    {
        var linked = _linkedCts.Token;
        if (!callerToken.CanBeCanceled || callerToken == linked || callerToken == _externalToken)
        {
            return linked;
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

    /// <summary>预算在构造时即耗尽：确定性失败，不与「已到达的字节」赛跑。</summary>
    private void ThrowIfBudgetExhausted()
    {
        if (_budgetExhausted)
        {
            throw AxiosException.Timeout(_config, _request);
        }
    }

    /// <summary>
    /// 把取消翻译成库的错误码：超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>；
    /// 调用方自己的令牌取消时什么也不做，由调用处的 <c>throw;</c> 原样抛出。
    /// </summary>
    private void ThrowIfTimedOutOrCanceled(CancellationToken callerToken, Exception inner)
    {
        if (callerToken.IsCancellationRequested)
        {
            return;
        }

        if (_externalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(_config, _request, inner);
        }

        if (_timeoutCts.IsCancellationRequested)
        {
            // 空闲超时与整体预算超时分开报。
            if (_idleTimeout > TimeSpan.Zero)
            {
                throw AxiosException.StreamIdleTimeout(_config, _request, _idleTimeout, inner);
            }

            throw AxiosException.Timeout(_config, _request, inner);
        }
    }

    /// <summary>释放链接源与超时计时器；内层流由基类释放。</summary>
    protected override void OnReleased()
    {
        _cachedCallerCts?.Dispose();
        _linkedCts.Dispose();
        _timeoutCts.Dispose();
    }
}

/// <summary>
/// 流式响应的所有权包装：把连接池租约、<see cref="HttpResponseMessage"/> 与并发槽位的生命周期
/// 绑定到调用方手里的响应流上，流 Dispose 时才一并释放（提前归还会让空闲判定与并发上限失真）。
/// 注入自定义 Handler 的实例没有租约，<c>lease</c> 传 <c>null</c>。
/// </summary>
internal sealed class ResponseStreamOwner : DelegatingStream
{
    private readonly HttpResponseMessage? _message;
    private readonly PoolLease? _lease;
    private readonly IDisposable? _slot;

    public ResponseStreamOwner(Stream inner, HttpResponseMessage? message, PoolLease? lease, IDisposable? slot = null)
        : base(inner)
    {
        _message = message;
        _lease = lease;
        _slot = slot;
    }

    /// <summary>释放响应消息、租约与并发槽位；前一个抛异常也不跳过其余释放。内层流由基类释放。</summary>
    protected override void OnReleased()
    {
        try
        {
            _message?.Dispose();
        }
        finally
        {
            try
            {
                _lease?.Dispose();
            }
            finally
            {
                _slot?.Dispose();
            }
        }
    }
}
