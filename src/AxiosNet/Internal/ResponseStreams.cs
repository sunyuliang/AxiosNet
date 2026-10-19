using AxiosNet.Pooling;

namespace AxiosNet.Internal;

/// <summary>
/// 流式响应的超时包装：把「请求总超时」延续到响应体读取阶段。
/// <para>
/// 非流式响应在库内部就把正文读完，<see cref="AxiosConfig.Timeout"/> 天然覆盖；
/// 流式响应把正文交给调用方读取，若不额外包一层，超时在拿到响应头后就失效了。
/// 这里用剩余的 timeout 建一个 CTS，并在每次异步读取时与外部取消令牌一起使用：
/// 超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>，调用方自己的 token 取消则原样抛
/// <see cref="OperationCanceledException"/>。
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
    /// 同步读无法在等待中被打断（超时/取消只在实际发起读取前检查），
    /// 需要可中断的读取请用 <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>。
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureNotCanceled();
        return _inner.Read(buffer, offset, count);
    }

    public override int ReadByte()
    {
        EnsureNotCanceled();
        return _inner.ReadByte();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(
            token => _inner.ReadAsync(buffer, offset, count, token),
            cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => new(ReadCoreAsync(token => _inner.ReadAsync(buffer, token).AsTask(), cancellationToken));

    private async Task<int> ReadCoreAsync(
        Func<CancellationToken, Task<int>> read,
        CancellationToken callerToken)
    {
        CancellationTokenSource? perReadCts = null;
        try
        {
            var token = _linkedCts.Token;
            if (callerToken.CanBeCanceled)
            {
                perReadCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, token);
                token = perReadCts.Token;
            }

            return await read(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested)
            {
                throw;
            }

            if (_externalToken.IsCancellationRequested)
            {
                throw AxiosException.Canceled(_config, _request);
            }

            if (_timeoutCts.IsCancellationRequested)
            {
                throw AxiosException.Timeout(_config, _request);
            }

            throw;
        }
        finally
        {
            perReadCts?.Dispose();
        }
    }

    /// <summary>同步读路径的取消/超时预检：已取消时直接抛对应的 AxiosException。</summary>
    private void EnsureNotCanceled()
    {
        if (!_linkedCts.IsCancellationRequested)
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
