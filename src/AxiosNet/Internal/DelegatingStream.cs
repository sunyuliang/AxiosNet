namespace AxiosNet.Internal;

/// <summary>
/// 包装流基类：透传读 / 写 / Flush 与能力成员，同步入口阻塞等待子类的 <c>*CoreAsync</c>。
/// 释放默认连同内层流一起，附加资源在 <see cref="OnReleased"/> 释放。
/// </summary>
internal abstract class DelegatingStream : Stream
{
    private readonly Stream _inner;
    // 0/1：内层流只能释放一次。
    private int _disposed;

    protected DelegatingStream(Stream inner) => _inner = inner;

    /// <summary>内层流（只读）。</summary>
    protected Stream Inner => _inner;

    /// <summary>是否随本包装一起释放内层流；上传侧包装流（底层流归 HttpClient 所有）覆写为 false。</summary>
    protected virtual bool DisposeInnerStream => true;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    // 定位直接转给内层：本层只管读写，位置语义不叠加。
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    // 改长度直接转给内层，本层不缓存长度（缓存了就得以内层为准同步）。
    public override void SetLength(long value) => _inner.SetLength(value);

    // 同步读复用异步核心再阻塞等待：包装流只维护一套逻辑，不写两遍。
    public override int Read(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // 旧版数组重载：转成 Memory 后和下面那条走同一个核心。
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // 新版 Memory 重载：直接进核心，不再包一层 Task。
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadCoreAsync(buffer, cancellationToken);

    /// <summary>读核心：默认透传内层流；包装流在此叠加超时 / 进度 / 限速语义。</summary>
    protected virtual ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, cancellationToken);

    // 同步写同样复用异步核心再阻塞等待。
    public override void Write(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // 旧版数组重载：转成 Memory 后走同一个核心。
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // 新版 Memory 重载：直接进核心。
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteCoreAsync(buffer, cancellationToken);

    /// <summary>写核心：默认透传内层流。</summary>
    protected virtual ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, cancellationToken);

    // 同步 Flush 复用异步核心再阻塞等待。
    public override void Flush()
        => BlockingWait.Wait(FlushCoreAsync(CancellationToken.None));

    // 异步 Flush 转核心。
    public override Task FlushAsync(CancellationToken cancellationToken)
        => FlushCoreAsync(cancellationToken).AsTask();

    /// <summary>Flush 核心：默认透传内层流（FlushAsync 返回 Task，显式包成 ValueTask）。</summary>
    protected virtual ValueTask FlushCoreAsync(CancellationToken cancellationToken)
        => new ValueTask(_inner.FlushAsync(cancellationToken));

    // 只在第一次释放时关内层并回调 OnReleased：Interlocked 换位保证重复 Dispose 不再触发。
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                if (DisposeInnerStream)
                {
                    _inner.Dispose();
                }
            }
            finally
            {
                OnReleased();
            }
        }

        base.Dispose(disposing);
    }

    // 异步释放：先异步关内层，同样只执行一次，最后交给基类。
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                if (DisposeInnerStream)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                OnReleased();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>释放附加资源（CTS / 租约 / 响应消息）；在内层流释放之后调用，抛异常不得跳过其余释放。</summary>
    protected virtual void OnReleased()
    {
    }
}
