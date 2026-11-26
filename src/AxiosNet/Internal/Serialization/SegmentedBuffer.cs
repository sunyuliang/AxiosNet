using System.Text;

namespace AxiosNet.Internal.Serialization;

/// <summary>流式分帧的读窗口：一块可压缩、可增长的字节缓冲，加上「未消费区间」与 EOF 标记。</summary>
/// <remarks>
/// 未消费区间是 <see cref="Start"/> 到 <see cref="End"/>（左闭右开）。<see cref="FillAsync"/> 会把未消费字节搬到缓冲开头，
/// 于是既有的相对下标要按 <see cref="CompactionShift"/> 自行前移。缓冲到达 <c>maxBytes</c> 仍装不下时抛
/// <c>tooLarge</c> 造出来的那个异常（帧过大或元素过大的文案由分帧模式决定），不无限增长。
/// </remarks>
internal sealed class SegmentedBuffer
{
    /// <summary>每次从流里读的块大小，也是扩容与溢写缓冲的基数。</summary>
    internal const int ChunkSize = 8 * 1024;

    private readonly Stream _stream;
    private readonly int _maxBytes;
    private readonly Func<int, AxiosException> _tooLarge;

    private byte[] _buffer = new byte[ChunkSize];
    private int _start;
    private int _end;
    private bool _eof;

    /// <summary>包住指定响应流。</summary>
    /// <param name="stream">响应正文流（只读一次）。</param>
    /// <param name="maxBytes">缓冲上限，同时是单帧 / 单元素的判定上限。</param>
    /// <param name="tooLarge">到达上限时造异常：参数是上限字节数。</param>
    internal SegmentedBuffer(Stream stream, int maxBytes, Func<int, AxiosException> tooLarge)
    {
        _stream = stream;
        _maxBytes = maxBytes;
        _tooLarge = tooLarge;
    }

    /// <summary>未消费区间的起点（缓冲区下标）。</summary>
    internal int Start => _start;

    /// <summary>未消费区间的终点（缓冲区下标，开区间）。</summary>
    internal int End => _end;

    /// <summary>流已读完：<see cref="End"/> 之后不会再有新字节。</summary>
    internal bool EndOfStream => _eof;

    /// <summary>最近一次 <see cref="FillAsync"/> 把窗口左移了多少字节；<c>0</c> 表示没有压缩。</summary>
    internal int CompactionShift { get; private set; }

    /// <summary>未消费区间。</summary>
    internal ReadOnlySpan<byte> Window => _buffer.AsSpan(_start, _end - _start);

    /// <summary>缓冲区里的任意区间（分帧状态机按已记下的绝对下标取）。</summary>
    /// <param name="offset">起点下标。</param>
    /// <param name="length">字节数。</param>
    internal ReadOnlySpan<byte> Span(int offset, int length) => _buffer.AsSpan(offset, length);

    /// <summary>缓冲区里的任意区间，以内存块形式交出（帧载荷直接引用它，不复制）。</summary>
    /// <param name="offset">起点下标。</param>
    /// <param name="length">字节数。</param>
    internal ReadOnlyMemory<byte> Memory(int offset, int length) => _buffer.AsMemory(offset, length);

    /// <summary>消费掉窗口开头的若干字节。</summary>
    /// <param name="count">字节数。</param>
    internal void Advance(int count) => _start += count;

    /// <summary>把窗口起点移到指定下标（行分帧跳过行尾时用）。</summary>
    /// <param name="offset">新的起点下标。</param>
    internal void MoveStart(int offset) => _start = offset;

    /// <summary>把区间拷到目标数组（跨块元素往溢写缓冲追加）。</summary>
    /// <param name="offset">源起点下标。</param>
    /// <param name="length">字节数。</param>
    /// <param name="destination">目标数组。</param>
    /// <param name="destinationIndex">目标写入位置。</param>
    internal void CopyTo(int offset, int length, byte[] destination, int destinationIndex)
        => Buffer.BlockCopy(_buffer, offset, destination, destinationIndex, length);

    /// <summary>按已解析的编码解码某个区间；空区间返回空串。</summary>
    /// <param name="offset">起点下标。</param>
    /// <param name="length">字节数。</param>
    /// <param name="encoding">生效编码；<c>null</c> 时按默认 UTF-8。</param>
    internal string Decode(int offset, int length, Encoding? encoding)
        => length <= 0 ? string.Empty : ResponseTextDecoder.DecodeResolved(_buffer.AsSpan(offset, length), encoding);

    /// <summary>压缩未消费字节后补读一块；读到 0 字节置 <see cref="EndOfStream"/>。</summary>
    /// <param name="token">枚举令牌。</param>
    internal async ValueTask FillAsync(CancellationToken token)
    {
        CompactionShift = 0;

        if (_start > 0)
        {
            var remaining = _end - _start;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
            }

            CompactionShift = _start;
            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= _maxBytes)
            {
                // 单块已经顶到上限：再读也装不下，按「帧 / 元素过大」终止。
                throw _tooLarge(_maxBytes);
            }

            var grown = new byte[Math.Min(_buffer.Length * 2, _maxBytes)];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _end);
            _buffer = grown;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
        }
        else
        {
            _end += read;
        }
    }
}
