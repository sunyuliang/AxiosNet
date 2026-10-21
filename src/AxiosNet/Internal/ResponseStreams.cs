using AxiosNet.Pooling;

namespace AxiosNet.Internal;

/// <summary>
/// 流式响应的正文包装：把「请求总超时」延续到正文读取阶段，并统一正文阶段的取消 / IO 异常。
/// <para>
/// 非流式响应在库内读完正文，<see cref="AxiosConfig.Timeout"/> 天然覆盖；流式响应把正文交给调用方，
/// 不包一层超时在拿到响应头后就失效了。这里用剩余预算建 CTS 并与外部取消链接，交给底层每次读写：
/// 超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>，调用方自己的令牌取消则原样抛出。
/// </para>
/// <para>
/// <b>无条件包装</b>（<see cref="AxiosConfig.Timeout"/> 为 0 时 <c>remaining</c> 传 <c>null</c>）：
/// 异常翻译不能只在「设了超时」时生效，否则同一断连场景会因 Timeout 取值给出不同异常类型。
/// </para>
/// <para>
/// 令牌必须交给<b>底层那一次 I/O</b>，而不是发起前判断一次。同步入口没有令牌可用，统一实现为
/// 「对异步核心的阻塞等待」（链路全程 <c>ConfigureAwait(false)</c>，UI 线程上也不会死锁），但终究占一个线程。
/// </para>
/// </summary>
internal sealed class TimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage _request;
    private readonly CancellationToken _externalToken;
    private readonly CancellationTokenSource _timeoutCts;
    private readonly CancellationTokenSource _linkedCts;

    /// <summary>
    /// 构造时预算就已耗尽（响应头阶段用完了整个 timeout）：核心方法入口直接抛 <c>ECONNABORTED</c>。
    /// <para>
    /// 不用 <c>CancelAfter(0)</c> 抢跑：底层可能还躺着已到达的字节，那样同一次 Read 可能读到数据、
    /// 也可能抛超时（计时器与读取赛跑）。这里改成确定性失败。
    /// </para>
    /// </summary>
    private readonly bool _budgetExhausted;

    /// <summary>
    /// 最近一个「调用方自备可取消令牌」对应的链接源缓存：循环 <c>ReadAsync(buf, ct)</c> 读大文件时
    /// ct 每块都一样，没有这层缓存就会每块新建 CTS + 两次注册 + dispose。
    /// </summary>
    private CancellationToken _cachedCallerToken;

    private CancellationTokenSource? _cachedCallerCts;

    private int _disposed;

    /// <summary>
    /// 包装流式正文。<paramref name="remaining"/> 为剩余超时预算，<c>null</c> 表示不超时
    /// （仍处理外部取消与 IO 异常翻译）。
    /// </summary>
    public TimeoutStream(
        Stream inner,
        TimeSpan? remaining,
        CancellationToken externalToken,
        AxiosConfig config,
        HttpRequestMessage request)
    {
        _inner = inner;
        _config = config;
        _request = request;
        _externalToken = externalToken;

        _timeoutCts = new CancellationTokenSource();
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

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>
    /// 同步读：阻塞等待异步核心，不走 <c>_inner.Read</c>，免得出现「读前预检一次、读中就断不掉」的洞。
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // ReadByte / Read(Span<byte>) 不覆写：基类实现会各自换算成 Read(byte[], int, int)，
    // 同样落到上面这条路径，不需要维护第二套同步语义。

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadCoreAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var (token, perCallCts) = Link(callerToken);
        try
        {
            return await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (IOException ex)
        {
            // 正文读到一半连接断了（HttpIOException / RST / 长度不符）：与非流式路径保持同一错误模型。
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    /// <summary>同步写：与同步读同理，阻塞等待带令牌的异步写。</summary>
    public override void Write(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteCoreAsync(buffer, cancellationToken);

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var (token, perCallCts) = Link(callerToken);
        try
        {
            await _inner.WriteAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (IOException ex)
        {
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    public override void Flush() => BlockingWait.Wait(FlushCoreAsync(CancellationToken.None));

    public override Task FlushAsync(CancellationToken cancellationToken) => FlushCoreAsync(cancellationToken).AsTask();

    private async ValueTask FlushCoreAsync(CancellationToken callerToken)
    {
        ThrowIfBudgetExhausted();

        var (token, perCallCts) = Link(callerToken);
        try
        {
            await _inner.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThrowIfTimedOutOrCanceled(callerToken, ex);
            throw;
        }
        catch (IOException ex)
        {
            throw AxiosException.Network(_config, _request, ex);
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    /// <summary>
    /// 把本次调用传入的令牌链接到「外部取消 + 超时」上。调用方没传令牌、或传的就是已知的那两个源时
    /// 直接复用预建的链接源，避免每次读写都新建 CTS。
    /// <para>
    /// 反复传<b>同一个</b>自备令牌时同样复用（按「最近一个 callerToken」缓存）；换令牌才释放上一个
    /// —— 单读者顺序读保证它的读写都已返回。多读者并发读同一条流违反本类的使用契约。
    /// </para>
    /// </summary>
    private (CancellationToken Token, CancellationTokenSource? Cts) Link(CancellationToken callerToken)
    {
        var linked = _linkedCts.Token;
        if (!callerToken.CanBeCanceled || callerToken == linked || callerToken == _externalToken)
        {
            return (linked, null);
        }

        if (callerToken == _cachedCallerToken && _cachedCallerCts is not null)
        {
            return (_cachedCallerCts.Token, null);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, linked);
        var previous = _cachedCallerCts;
        _cachedCallerToken = callerToken;
        _cachedCallerCts = cts;

        // 释放被替换掉的旧链接源：单读者顺序读保证它的读写已全部返回。
        previous?.Dispose();
        return (cts.Token, null);
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
    /// 把取消翻译成库的错误码：超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>。
    /// 调用方自己的令牌取消时什么也不做，由调用处的 <c>throw;</c> 原样抛出（保留原始堆栈）。
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
            throw AxiosException.Timeout(_config, _request, inner);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                _cachedCallerCts?.Dispose();
                _linkedCts.Dispose();
                _timeoutCts.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _cachedCallerCts?.Dispose();
                _linkedCts.Dispose();
                _timeoutCts.Dispose();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 流式响应的所有权包装：把「连接池租约」与「<see cref="HttpResponseMessage"/>」的生命周期
/// 绑定到调用方手里的响应流上。
/// <para>
/// 非流式响应读完正文后立刻归还租约并释放消息；流式响应的正文还在调用方手里，
/// 此时归还租约会让「该 origin 空闲」成立，后台驱逐可能把仍在下载的节点回收掉。
/// 因此租约延后到流 Dispose 时归还，统计里的 ActiveLeases 也反映真实在读的下载数。
/// </para>
/// <para>
/// 同理消息本体也不能提前释放（正文流从它取出），一并在流 Dispose 时释放。
/// 注入自定义 Handler 的实例没有租约，<c>lease</c> 传 <c>null</c> 即可。
/// </para>
/// </summary>
internal sealed class ResponseStreamOwner : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage? _message;
    private readonly PoolLease? _lease;
    private int _disposed;

    public ResponseStreamOwner(Stream inner, HttpResponseMessage? message, PoolLease? lease)
    {
        _inner = inner;
        _message = message;
        _lease = lease;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    // 同步成员一律阻塞等待对应的异步成员（链路全 ConfigureAwait(false)），不把同步 I/O 压给内层流：
    // 即使注入的自定义 Handler 返回 async-only 的流，同步读也能工作。
    // ReadByte / Read(Span<byte>) 等不覆写：基类实现会换算成 Read(byte[], int, int)，落到同一路径。
    public override int Read(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(_inner.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    public override void Write(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(_inner.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // WriteAsync 必须显式覆写：基类实现会退化成同步 Write（再阻塞等待一次），
    // 异步调用方会在线程池上白等一次同步-over-async。
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush()
        => BlockingWait.Wait(_inner.FlushAsync(CancellationToken.None));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseAll();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseOwnership();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ReleaseAll()
    {
        try
        {
            _inner.Dispose();
        }
        finally
        {
            ReleaseOwnership();
        }
    }

    /// <summary>释放响应消息与租约；两者都不能因为前一个抛异常而被跳过。</summary>
    private void ReleaseOwnership()
    {
        try
        {
            _message?.Dispose();
        }
        finally
        {
            _lease?.Dispose();
        }
    }
}
