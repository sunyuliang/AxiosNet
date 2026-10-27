namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 只读、非 seekable 的流：模拟网络流 / 管道流（<see cref="Stream.Length"/> 不可知），
/// 用来覆盖「内容长度未知时手写 Content-Length」的逃生口。
/// </summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableStream(byte[] payload) => _inner = new MemoryStream(payload, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
