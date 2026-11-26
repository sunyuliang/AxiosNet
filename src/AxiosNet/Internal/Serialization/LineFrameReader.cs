namespace AxiosNet.Internal.Serialization;

/// <summary>行协议分帧：NDJSON 一行一帧，SSE 以空行收尾、多条 <c>data:</c> 用 <c>\n</c> 连接。</summary>
/// <remarks>
/// 字节层按 CR / LF / CRLF 断行（<see cref="SegmentedBuffer"/> 负责补读与压缩）；行首 UTF-8 BOM 不进正文。
/// SSE 的 <c>[DONE]</c> 与心跳空行都不产出帧；单帧上限按原始字节数判定。
/// </remarks>
internal sealed class LineFrameReader : IFrameReader
{
    /// <summary>SSE 的结束哨兵：收到即视为流结束。</summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>一帧收尾后可留用的帧缓冲容量（字节）。</summary>
    private const int MaxRetainedFrameBytes = 8 * SegmentedBuffer.ChunkSize;

    private readonly SegmentedBuffer _buffer;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage? _request;
    private readonly AxiosStreamFormat _format;
    private readonly int _maxFrameBytes;

    /// <summary>SSE 帧正文的累积缓冲（原始字节）。</summary>
    private byte[] _frameBuffer = Array.Empty<byte>();

    private int _frameLength;

    /// <summary>当前 SSE 帧已累积的原始字节数（分帧上限按字节判定）。</summary>
    private int _frameBytes;

    /// <summary>包住指定读窗口。</summary>
    /// <param name="buffer">共享读窗口。</param>
    /// <param name="config">本次请求的生效配置（造异常用）。</param>
    /// <param name="request">发起这条流的请求（造异常用）。</param>
    /// <param name="format">行协议的具体形态：NDJSON 或 SSE。</param>
    /// <param name="maxFrameBytes">单帧字节上限。</param>
    internal LineFrameReader(
        SegmentedBuffer buffer,
        AxiosConfig config,
        HttpRequestMessage? request,
        AxiosStreamFormat format,
        int maxFrameBytes)
    {
        _buffer = buffer;
        _config = config;
        _request = request;
        _format = format;
        _maxFrameBytes = maxFrameBytes;
    }

    /// <inheritdoc />
    public async ValueTask<FramePayload?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            if (await TryReadLineAsync(token).ConfigureAwait(false) is not { } line)
            {
                // EOF：SSE 可能没写最后的空行，残留的 data 也要交付一次。
                if (_format == AxiosStreamFormat.ServerSentEvents && _frameLength > 0)
                {
                    return TakeFrame();
                }

                return null;
            }

            var payload = HandleLine(line, out var done);
            if (done)
            {
                return payload;
            }
        }
    }

    /// <summary>按行格式解析一行原始字节；<paramref name="done"/> 为 <c>false</c> 时本行不产生帧，继续读下一行。</summary>
    /// <param name="line">本行的位置信息。</param>
    /// <param name="done">本行是否结束了一帧。</param>
    private FramePayload? HandleLine(Line line, out bool done)
    {
        done = false;

        var span = _buffer.Span(line.Offset, line.Length);

        if (_format == AxiosStreamFormat.NewlineDelimitedJson)
        {
            if (span.Length == 0)
            {
                return null;
            }

            done = true;
            return FramePayload.FromBytes(_buffer.Memory(line.Offset, line.Length));
        }

        // SSE：空行结束一帧
        if (span.Length == 0)
        {
            if (_frameLength == 0)
            {
                // 心跳帧没有 data，跳过。
                return null;
            }

            done = true;
            return TakeFrame();
        }

        if (span[0] == (byte)':')
        {
            return null;
        }

        var colon = span.IndexOf((byte)':');
        var field = colon < 0 ? span : span[..colon];
        if (!IsAscii(field, "data"))
        {
            // event: / id: / retry: 等字段不进入数据流。
            return null;
        }

        var value = colon < 0 ? ReadOnlySpan<byte>.Empty : span[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
        {
            value = value[1..];
        }

        AppendFrame(value);

        // 上限按字节数。
        _frameBytes += line.ByteLength;
        if (_frameBytes > _maxFrameBytes)
        {
            throw AxiosException.StreamFrameTooLarge(_config, _request, "SSE", _maxFrameBytes);
        }

        return null;
    }

    /// <summary>取走当前累积的 SSE 帧并重置帧状态；<c>[DONE]</c> 返回 <c>null</c>（流结束）。</summary>
    private FramePayload? TakeFrame()
    {
        var frame = new ReadOnlyMemory<byte>(_frameBuffer, 0, _frameLength);
        _frameLength = 0;
        if (_frameBuffer.Length > MaxRetainedFrameBytes)
        {
            _frameBuffer = Array.Empty<byte>();
        }

        _frameBytes = 0;
        return IsAscii(frame.Span, DoneSentinel) ? null : FramePayload.FromBytes(frame);
    }

    /// <summary>把一条 data 行的值接到帧缓冲上（多条 data 行用 <c>\n</c> 连接）。</summary>
    /// <param name="value">本行 <c>data:</c> 之后的内容。</param>
    private void AppendFrame(ReadOnlySpan<byte> value)
    {
        if (_frameLength > 0)
        {
            EnsureFrameCapacity(_frameLength + 1);
            _frameBuffer[_frameLength++] = (byte)'\n';
        }

        if (value.IsEmpty)
        {
            return;
        }

        EnsureFrameCapacity(_frameLength + value.Length);
        value.CopyTo(_frameBuffer.AsSpan(_frameLength));
        _frameLength += value.Length;
    }

    /// <summary>帧缓冲不够就扩：取「需要的量」与「当前两倍」的较大者。</summary>
    /// <param name="required">需要的容量。</param>
    private void EnsureFrameCapacity(int required)
    {
        if (required <= _frameBuffer.Length)
        {
            return;
        }

        var grown = new byte[Math.Max(required, Math.Max(256, _frameBuffer.Length * 2))];
        Buffer.BlockCopy(_frameBuffer, 0, grown, 0, _frameLength);
        _frameBuffer = grown;
    }

    /// <summary>读取一行（不含行尾 CR / LF）及其原始字节数；<c>null</c> 表示流已结束。</summary>
    /// <param name="token">枚举令牌。</param>
    private async ValueTask<Line?> TryReadLineAsync(CancellationToken token)
    {
        while (true)
        {
            if (TryTakeLine(out var line))
            {
                return line;
            }

            if (_buffer.EndOfStream)
            {
                return TakeTail();
            }

            await _buffer.FillAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>窗口已含完整一行时取走它（CR / LF / CRLF 都是合法行尾）。</summary>
    /// <param name="line">取到的行。</param>
    /// <returns><c>false</c> = 窗口里还没有完整的一行，需要补读。</returns>
    private bool TryTakeLine(out Line? line)
    {
        line = null;

        var window = _buffer.Window;
        var terminator = window.IndexOfAny((byte)'\r', (byte)'\n');
        if (terminator < 0)
        {
            return false;
        }

        // CRLF 的 CR 落在窗口末尾：还判不出行尾长度，等下一块。
        if (window[terminator] == (byte)'\r' && terminator == window.Length - 1 && !_buffer.EndOfStream)
        {
            return false;
        }

        var byteLength = terminator;
        var offset = _buffer.Start;
        var length = byteLength;
        SkipBom(ref offset, ref length);
        _buffer.MoveStart(_buffer.Start + byteLength + 1);

        // 裸 CR / 裸 LF 只前进 1 字节；CRLF 再吃掉 LF。
        var rest = _buffer.Window;
        if (window[terminator] == (byte)'\r' && rest.Length > 0 && rest[0] == (byte)'\n')
        {
            _buffer.Advance(1);
        }

        line = new Line(offset, length, byteLength);
        return true;
    }

    /// <summary>EOF 时交付残留的尾巴（最后一行没有行尾）；窗口已空时返回 <c>null</c>。</summary>
    private Line? TakeTail()
    {
        var window = _buffer.Window;
        if (window.Length == 0)
        {
            return null;
        }

        var rawLength = window.Length;
        var offset = _buffer.Start;
        var length = rawLength;
        SkipBom(ref offset, ref length);
        _buffer.Advance(rawLength);
        return new Line(offset, length, rawLength);
    }

    /// <summary>行首的 UTF-8 BOM 不进入帧内容（与解码路径一致）；<paramref name="length"/> 是原始字节数，另计。</summary>
    /// <param name="offset">行首下标（会前移）。</param>
    /// <param name="length">行内容字节数（会减少）。</param>
    private void SkipBom(ref int offset, ref int length)
    {
        if (BomDetection.HasUtf8Bom(_buffer.Span(offset, length)))
        {
            offset += 3;
            length -= 3;
        }
    }

    /// <summary>ASCII 常量比较（字段名 / <c>[DONE]</c> 哨兵）：不产生字符串。</summary>
    /// <param name="span">待比较的字节。</param>
    /// <param name="value">ASCII 期望值。</param>
    private static bool IsAscii(ReadOnlySpan<byte> span, string value)
    {
        if (span.Length != value.Length)
        {
            return false;
        }

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != value[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>一行在缓冲区里的字节区间 + 原始字节流中的长度（SSE 分帧上限按字节判定）。</summary>
    /// <param name="Offset">行首在缓冲区里的下标。</param>
    /// <param name="Length">行内容的字节数，不含行尾。</param>
    /// <param name="ByteLength">连同行尾的字节数。</param>
    private readonly record struct Line(int Offset, int Length, int ByteLength);
}
