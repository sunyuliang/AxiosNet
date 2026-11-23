using AxiosNet.Pooling;

namespace AxiosNet.Internal.Response;

/// <summary>
/// 流式响应的正文包装：把本次尝试的剩余预算延续到正文读取阶段，并统一正文阶段的取消 / IO 异常。
/// </summary>
/// <remarks>
/// 超时 → <c>ECONNABORTED</c>，其余取消 → <c>ERR_CANCELED</c>，原始异常留在 <c>Cause</c>。
/// 无条件包装（<see cref="AxiosConfig.Timeout"/> 为 0 时预算传 <c>null</c>）；两个令牌源都按需创建，
/// 既无预算又无外部令牌时不建 CTS。
/// </remarks>
internal sealed class TimeoutStream : DelegatingStream
{
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage _request;
    private readonly CancellationToken _externalToken;

    /// <summary>超时计时源：只有「有剩余预算」时才创建，否则为 <c>null</c>。</summary>
    private readonly CancellationTokenSource? _timeoutCts;

    /// <summary>监听「外部令牌 + 计时器」的链接源：只在两者都成立时才创建，否则为 <c>null</c>。</summary>
    private readonly CancellationTokenSource? _linkedCts;

    /// <summary>没有调用方自备令牌时交给底层的令牌（已经算好，避免每次读都重新判断）。</summary>
    private readonly CancellationToken _token;

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

        // 预算是一次性的：从拿到响应头开始算到正文读完，中途不重置（没有独立的空闲计时）。
        // 只有真需要计时才建计时源；预算为 0 / 负数时既不建源也不设时，只记下「已耗尽」。
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
            // 不需要计时：外部令牌可取消就直接把它当本层令牌 —— 再包一层「只链接它」的源语义完全一样
            //（取消只可能来自它），只是多付一次 CTS；不可取消时整条路径都不需要任何取消能力，
            // 连令牌都不换（一个 CTS 都不建）。
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
            // 只有计时器：多建一个链接源没有意义，直接用计时源自己的令牌。
            _token = _timeoutCts.Token;
        }
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // 正文读到一半连接断了：与非流式路径同一错误模型。
            throw Translate(ex, callerToken);
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            throw Translate(ex, callerToken);
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            throw Translate(ex, callerToken);
        }
    }

    /// <summary>
    /// 把本次调用的令牌链接到「外部取消 + 超时」上：无令牌或已知令牌直接复用预建的链接源，
    /// 反复传同一个自备令牌时按缓存复用，换令牌才释放上一个。
    /// 调用方不持有任何要释放的 CTS。
    /// </summary>
    private CancellationToken Link(CancellationToken callerToken)
    {
        var linked = _token;
        if (!callerToken.CanBeCanceled || callerToken == linked || callerToken == _externalToken)
        {
            return linked;
        }

        // 本层没有任何取消来源（既不限时也没有外部令牌）：调用方令牌本身就是唯一来源，
        // 不必再包一层链接源 —— 语义相同且省一次分配。
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

    /// <summary>
    /// 预算在构造时即耗尽：确定性失败，不与已到达的字节赛跑。归属按 <c>Caller → Canceled → Timeout</c> 判定，
    /// 取消 → <c>ERR_CANCELED</c>，超时 → <c>ECONNABORTED</c>，取消分支保留原始 OCE 作 <c>Cause</c>。
    /// </summary>
    private void ThrowIfBudgetExhausted(CancellationToken callerToken)
    {
        if (!_budgetExhausted)
        {
            return;
        }

        switch (CancellationClassifier.Classify(_externalToken, _timeoutCts?.Token, callerToken))
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
    /// 把内层流的失败翻译成库的错误码：超时 → <c>ECONNABORTED</c>，其余取消（外部取消 / 调用方自备令牌取消 /
    /// 无已知令牌的内部 OCE）→ <c>ERR_CANCELED</c>，IO 与传输失败 → <c>ERR_NETWORK</c>，原始异常留在 <c>Cause</c>。
    /// </summary>
    /// <param name="failure">内层流抛出的异常。</param>
    /// <param name="callerToken">本次调用的调用方令牌，用于判定取消归属。</param>
    private Exception Translate(Exception failure, CancellationToken callerToken)
    {
        if (failure is not OperationCanceledException canceled)
        {
            return AxiosException.Network(_config, _request, failure);
        }

        return CancellationClassifier.Classify(_externalToken, _timeoutCts?.Token, callerToken) == CancellationKind.Timeout
            ? AxiosException.Timeout(_config, _request, canceled, _effectiveTimeout)
            : AxiosException.Canceled(_config, _request, canceled);
    }

    /// <summary>
    /// 释放链接源与超时计时器；内层流由基类释放。
    /// 两个源都是按需创建的，所以只用 <c>?.</c> 释放实际建过的那个，重复 Dispose 也安全。
    /// </summary>
    protected override void OnReleased()
    {
        _cachedCallerCts?.Dispose();
        _linkedCts?.Dispose();
        _timeoutCts?.Dispose();
    }
}

/// <summary>
/// 流式响应的所有权包装：把连接池租约、<see cref="HttpResponseMessage"/> 与上下文里登记的跨终端租约
/// （并发槽位等）的生命周期绑定到调用方手里的响应流上，流 Dispose 时一并释放。
/// 注入自定义 Handler 的实例没有租约，<c>lease</c> 传 <c>null</c>。
/// </summary>
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

    /// <summary>释放响应消息、连接租约与登记的跨终端租约；前一个抛异常也不跳过其余释放。内层流由基类释放。</summary>
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
                _leases?.Dispose();
            }
        }
    }
}
