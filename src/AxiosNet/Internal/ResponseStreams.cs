using AxiosNet.Pooling;

namespace AxiosNet.Internal;

/// <summary>
/// 流式响应的正文包装：把本次尝试的剩余预算延续到正文读取阶段，并统一正文阶段的取消 / IO 异常。
/// </summary>
/// <remarks>
/// 用剩余预算建 CTS 并与外部取消链接：超时 → <c>ECONNABORTED</c>，其余取消（外部令牌 / 调用方自备令牌 /
/// 无已知令牌的 OCE）一律 → <c>ERR_CANCELED</c>，原始异常留在 <c>Cause</c>。无条件包装
/// （<see cref="AxiosConfig.Timeout"/> 为 0 时预算传 <c>null</c>），令牌交给底层那一次 I/O；
/// 同步入口实现为对异步核心的阻塞等待。
/// </remarks>
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
    /// 包装流式正文。<paramref name="remaining"/> 为本次尝试的剩余超时预算（已被 <c>TotalTimeout</c> 压过），
    /// <c>null</c> 表示不超时；构造时已耗尽则核心方法入口直接失败。
    /// <paramref name="effectiveTimeout"/> 只在「被总预算压小」时传入，用于让超时消息说清真实上限。
    /// </summary>
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

        _timeoutCts = new CancellationTokenSource();

        // 预算是一次性的：从拿到响应头开始算到正文读完，中途不重置（没有独立的空闲计时）。
        if (remaining is TimeSpan budget)
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

    /// <summary>被总预算压小后的真实上限（未压小为 <c>null</c>），只用于错误消息。</summary>
    private readonly TimeSpan? _effectiveTimeout;

    /// <summary>
    /// 读一块：内层同步完成时原样透传，真异步或故障才落到异步路径。
    /// </summary>
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken callerToken)
    {
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
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw AxiosException.Network(_config, _request, ex);
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
    }

    /// <summary>写：先查总预算，再把调用方令牌并进预算令牌后写入；取消与网络故障各归各的错误模型。</summary>
    protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted(callerToken);

        var token = Link(callerToken);
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
    }

    /// <summary>刷新：同样先查预算；取消按超时 / 取消翻译，IO 故障翻译成网络错误。</summary>
    protected override async ValueTask FlushCoreAsync(CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted(callerToken);

        var token = Link(callerToken);
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
    }

    /// <summary>
    /// 把本次调用的令牌链接到「外部取消 + 超时」上：无令牌或已知令牌直接复用预建的链接源，
    /// 反复传同一个自备令牌时按缓存复用，换令牌才释放上一个。
    /// 调用方不持有任何要释放的 CTS。
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

    /// <summary>
    /// 预算在构造时即耗尽：确定性失败，不与「已到达的字节」赛跑；归属仍按
    /// <c>Caller → Canceled → Timeout</c> 的同一优先级判定，但最终交给调用方的一律是带库错误码的
    /// <see cref="AxiosException"/>：取消（含调用方自备令牌）→ <c>ERR_CANCELED</c>，
    /// 超时 → <c>ECONNABORTED</c>。取消分支保留一个以对应令牌构造的原始 OCE 作 <c>Cause</c>。
    /// </summary>
    private void ThrowIfBudgetExhausted(CancellationToken callerToken)
    {
        if (!_budgetExhausted)
        {
            return;
        }

        switch (CancellationClassifier.Classify(_externalToken, _timeoutCts.Token, callerToken))
        {
            case CancellationKind.Caller:
                // 调用方自己的令牌已取消：优先级最高，但最终仍是 ERR_CANCELED（与普通请求路径同一出口）。
                throw AxiosException.Canceled(_config, _request, new OperationCanceledException(callerToken));

            case CancellationKind.Canceled:
                throw AxiosException.Canceled(_config, _request);

            default:
                throw AxiosException.Timeout(_config, _request, null, _effectiveTimeout);
        }
    }

    /// <summary>
    /// 把取消统一翻译成库的错误码：超时 → <c>ECONNABORTED</c>，其余（外部取消 / 调用方自备令牌取消 /
    /// 无已知令牌的内部 OCE）→ <c>ERR_CANCELED</c>，原始异常留在 <c>Cause</c>。
    /// 流式读取不再原样抛出 <see cref="OperationCanceledException"/>，调用方只按 <c>Code</c> 判断取消。
    /// </summary>
    private void ThrowIfTimedOutOrCanceled(CancellationToken callerToken, Exception inner)
    {
        switch (CancellationClassifier.Classify(_externalToken, _timeoutCts.Token, callerToken))
        {
            case CancellationKind.Timeout:
                throw AxiosException.Timeout(_config, _request, inner, _effectiveTimeout);

            default:
                // Caller / Canceled / None：都归一为 ERR_CANCELED。
                throw AxiosException.Canceled(_config, _request, inner);
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
/// 绑定到调用方手里的响应流上，流 Dispose 时一并释放。
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
