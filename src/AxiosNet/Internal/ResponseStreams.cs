using AxiosNet.Pooling;

namespace AxiosNet.Internal;

/// <summary>
/// 流式响应的超时包装：把「请求总超时」延续到响应体读取阶段。
/// <para>
/// 非流式响应在库内部就把正文读完，<see cref="AxiosConfig.Timeout"/> 天然覆盖；
/// 流式响应把正文交给调用方读取，若不额外包一层，超时在拿到响应头后就失效了。
/// 这里用剩余的 timeout 建一个 CTS，并把「外部取消 + 超时 + 本次调用传入的令牌」链接后
/// 交给底层的每一次读写/Flush：超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>，
/// 调用方自己的 token 取消则原样抛 <see cref="OperationCanceledException"/>。
/// </para>
/// <para>
/// 关键点：令牌必须交给**底层那一次 I/O**，而不是在发起前判断一次。
/// 同步入口（<see cref="Read(byte[], int, int)"/>、<see cref="Write(byte[], int, int)"/>、
/// <see cref="Flush"/>）本身没有令牌可用，因此统一实现为「对异步核心的阻塞等待」——
/// 令牌在等待期间照样生效，不会出现「已经超时了，但同步读还卡在 socket 上」这种洞。
/// 阻塞等待不会捕获同步上下文（链路内部全程 <c>ConfigureAwait(false)</c>），
/// 所以在 UI 线程上同步读也不会死锁——除非注入的自定义 Handler 提供的流自己捕获上下文。
/// 它终究占住一个线程，能用 async 就用 async。
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
    private int _disposed;

    public TimeoutStream(
        Stream inner,
        TimeSpan remaining,
        CancellationToken externalToken,
        AxiosConfig config,
        HttpRequestMessage request)
    {
        _inner = inner;
        _config = config;
        _request = request;
        _externalToken = externalToken;

        _timeoutCts = new CancellationTokenSource();
        _timeoutCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
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
    /// 同步读：阻塞等待异步核心。为了不出现「读前预检一次、读中就断不掉」的洞，
    /// 这里不再走 <c>_inner.Read</c>，而是把同一条带令牌的异步路径同步等待掉。
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
        => Wait(ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // ReadByte / Read(Span<byte>) 不覆写：基类实现会各自换算成 Read(byte[], int, int)，
    // 于是同样落到上面这条路径，不需要再维护第二套同步语义。

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadCoreAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken callerToken)
    {
        var (token, perCallCts) = Link(callerToken);
        try
        {
            return await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfTimedOutOrCanceled(callerToken);
            throw;
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    /// <summary>同步写：与同步读同理，阻塞等待带令牌的异步写。</summary>
    public override void Write(byte[] buffer, int offset, int count)
        => Wait(WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteCoreAsync(buffer, cancellationToken);

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerToken)
    {
        var (token, perCallCts) = Link(callerToken);
        try
        {
            await _inner.WriteAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfTimedOutOrCanceled(callerToken);
            throw;
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    public override void Flush() => Wait(FlushCoreAsync(CancellationToken.None));

    public override Task FlushAsync(CancellationToken cancellationToken) => FlushCoreAsync(cancellationToken).AsTask();

    private async ValueTask FlushCoreAsync(CancellationToken callerToken)
    {
        var (token, perCallCts) = Link(callerToken);
        try
        {
            await _inner.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfTimedOutOrCanceled(callerToken);
            throw;
        }
        finally
        {
            perCallCts?.Dispose();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    /// <summary>
    /// 把本次调用传入的令牌链接到「外部取消 + 超时」上。
    /// 调用方没传令牌、或传进来的就是已知的那两个源时直接复用预建的链接源，
    /// 避免每一次读写都新建一个 CTS（大文件下载时这是按缓冲区数量级的分配）。
    /// </summary>
    private (CancellationToken Token, CancellationTokenSource? Cts) Link(CancellationToken callerToken)
    {
        var linked = _linkedCts.Token;
        if (!callerToken.CanBeCanceled || callerToken == linked || callerToken == _externalToken)
        {
            return (linked, null);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, linked);
        return (cts.Token, cts);
    }

    /// <summary>
    /// 把取消翻译成库的错误码：超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>。
    /// 调用方自己的令牌取消时什么也不做，由调用处的 <c>throw;</c> 原样抛出
    /// <see cref="OperationCanceledException"/>（保留原始堆栈）。
    /// </summary>
    private void ThrowIfTimedOutOrCanceled(CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return;
        }

        if (_externalToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(_config, _request);
        }

        if (_timeoutCts.IsCancellationRequested)
        {
            throw AxiosException.Timeout(_config, _request);
        }
    }

    /// <summary>
    /// 同步入口的阻塞等待。已完成（内存流 / 数据已到达）时直接取结果，不为一次快速返回白建 Task。
    /// </summary>
    private static T Wait<T>(ValueTask<T> task)
        => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    private static void Wait(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            task.AsTask().GetAwaiter().GetResult();
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
                _linkedCts.Dispose();
                _timeoutCts.Dispose();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 把连接池租约的生命周期绑定到响应流上：
/// <para>
/// 非流式响应在读完正文后立刻归还租约；流式响应的正文还在调用方手里，
/// 此时归还租约会让「该 origin 空闲」成立，后台驱逐可能把仍在下载的连接节点回收掉。
/// 因此流式响应把租约延后到流 Dispose 时归还，同时让统计里的 ActiveLeases 反映真实在读的下载数。
/// </para>
/// </summary>
internal sealed class LeaseHoldingStream : Stream
{
    private readonly Stream _inner;
    private readonly PoolLease _lease;
    private int _disposed;

    public LeaseHoldingStream(Stream inner, PoolLease lease)
    {
        _inner = inner;
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

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int ReadByte() => _inner.ReadByte();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

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
                _lease.Dispose();
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
                _lease.Dispose();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
