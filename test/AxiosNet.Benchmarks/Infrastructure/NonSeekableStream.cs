namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 把任意流包装成不可定位的流：模拟网络流 / 管道流，用于测「无 <c>Content-Length</c>、走 chunked」的上传路径。
/// <para>读写全部转发给内层流，只把定位能力关掉；长度与位置一经询问即抛，避免上层误以为能算长度。</para>
/// </summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>转发同步读到内层流（定位能力已经被关掉）。</summary>
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <summary>转发 <c>Span</c> 版同步读到内层流。</summary>
    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    /// <summary>转发异步读到内层流。</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    /// <summary>转发数组版异步读到内层流。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <summary>不支持写侧操作：本包装只用于模拟「只能顺着读一次」的正文。</summary>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>禁止回拨：重试 / 换端点若要求 seek，就会在这里失败。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>不支持改长度：不可定位的流没有这个概念。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>不支持写入：读到一半的网络流不接受反向写。</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>释放内层流，并让基类走完释放流程。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
