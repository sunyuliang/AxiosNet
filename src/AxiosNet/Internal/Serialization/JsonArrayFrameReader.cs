using System.Text;
using System.Text.Json;

namespace AxiosNet.Internal.Serialization;

/// <summary>JSON 数组分帧：用 <see cref="Utf8JsonReader"/> 增量定位顶层元素区间，逐个交出。</summary>
/// <remarks>
/// 元素完全落在读窗口里时直接引用缓冲字节；跨块的元素先搬进溢写缓冲（<c>_elementSpill</c>）再一次交付，收尾后按
/// 上限释放。顶层必须是数组，空正文不算错误，读到 <c>]</c> 即结束且不校验尾部空白。
/// </remarks>
internal sealed class JsonArrayFrameReader : IFrameReader
{
    /// <summary>元素收尾后可留用的溢写缓冲上限（8 块）。</summary>
    private const int MaxRetainedSpill = 8 * SegmentedBuffer.ChunkSize;

    /// <summary>报错文案里的格式名。</summary>
    private const string FormatName = "JSON array";

    private readonly SegmentedBuffer _buffer;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage? _request;
    private readonly Encoding? _encoding;
    private readonly int _maxFrameBytes;

    /// <summary>帧是否以原始字节交给序列化器（UTF-8 为 true，声明其它编码时退回文本）。</summary>
    private readonly bool _byteFrames;

    /// <summary><see cref="Utf8JsonReader"/> 的跨块续读状态。</summary>
    private JsonReaderState _readerState;

    /// <summary>已处理过前导 BOM（只做一次）。</summary>
    private bool _headerChecked;

    /// <summary>已见到顶层 <c>[</c>。</summary>
    private bool _arrayStarted;

    /// <summary>已见到顶层 <c>]</c>：之后不再读流。</summary>
    private bool _arrayEnded;

    /// <summary>当前元素已开始、尚未收尾。</summary>
    private bool _inElement;

    /// <summary>当前元素在本缓冲区里的起点；<c>-1</c> 表示前缀已搬进 <see cref="_elementSpill"/>。</summary>
    private int _elementStart = -1;

    /// <summary>跨块元素已消费的前缀（懒分配）。</summary>
    private byte[]? _elementSpill;

    /// <summary><see cref="_elementSpill"/> 中的有效字节数。</summary>
    private int _elementSpilled;

    /// <summary>包住指定读窗口。</summary>
    /// <param name="buffer">共享读窗口。</param>
    /// <param name="config">本次请求的生效配置（造异常用）。</param>
    /// <param name="request">发起这条流的请求（造异常用）。</param>
    /// <param name="encoding">生效编码；<c>null</c> 按默认 UTF-8。</param>
    /// <param name="byteFrames"><c>true</c> 时元素按原始字节交付。</param>
    /// <param name="maxFrameBytes">单元素字节上限。</param>
    /// <param name="readerOptions">外层数组的 JSON 方言。</param>
    internal JsonArrayFrameReader(
        SegmentedBuffer buffer,
        AxiosConfig config,
        HttpRequestMessage? request,
        Encoding? encoding,
        bool byteFrames,
        int maxFrameBytes,
        JsonReaderOptions readerOptions)
    {
        _buffer = buffer;
        _config = config;
        _request = request;
        _encoding = encoding;
        _byteFrames = byteFrames;
        _maxFrameBytes = maxFrameBytes;
        _readerState = new JsonReaderState(readerOptions);
    }

    /// <inheritdoc />
    public async ValueTask<FramePayload?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            if (_arrayEnded)
            {
                return null;
            }

            if (!_headerChecked)
            {
                // BOM 判定最多需要 3 个字节。
                while (_buffer.End - _buffer.Start < 3 && !_buffer.EndOfStream)
                {
                    await FillAsync(token).ConfigureAwait(false);
                }

                _headerChecked = true;
                SkipBom();
            }

            if (_buffer.EndOfStream && IsWhitespace(_buffer.Start, _buffer.End))
            {
                // 数据在元素边界 / 空白上耗尽：空正文不当作错误。
                if (!_arrayStarted)
                {
                    _arrayEnded = true;
                    return null;
                }

                throw Malformed("JSON array stream ended before its closing ']'");
            }

            var payload = ScanWindow();
            if (payload is not null)
            {
                return payload;
            }

            if (_arrayEnded)
            {
                return null;
            }

            if (_buffer.EndOfStream)
            {
                // 兜底：EOF 之后绝不能再补读。
                throw Malformed(_arrayStarted
                    ? "JSON array stream ended before its closing ']'"
                    : "JSON array stream ended before its opening '['");
            }

            await FillAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>在当前字节窗口里定位下一个顶层元素；窗口读完仍未定位到时把消费进度写回。</summary>
    private FramePayload? ScanWindow()
    {
        var reader = new Utf8JsonReader(_buffer.Window, _buffer.EndOfStream, _readerState);

        try
        {
            while (reader.Read())
            {
                var token = reader.TokenType;
                if (token == JsonTokenType.Comment)
                {
                    // 注释 token 既不是元素，也不改变深度。
                    continue;
                }

                var depth = reader.CurrentDepth;

                if (!_arrayStarted)
                {
                    if (token != JsonTokenType.StartArray || depth != 0)
                    {
                        // 顶层不是数组。
                        throw AxiosException.StreamMalformed(
                            _config,
                            _request,
                            $"JSON array stream must start with a top-level '[' but found {token}",
                            new JsonException($"expected StartArray, got {token}"));
                    }

                    _arrayStarted = true;
                    continue;
                }

                if (token == JsonTokenType.EndArray && depth == 0)
                {
                    // 顶层数组收尾：立即结束，不校验尾部空白。
                    _readerState = reader.CurrentState;
                    _buffer.Advance((int)reader.BytesConsumed);
                    _arrayEnded = true;
                    return null;
                }

                if (!_inElement)
                {
                    // 顶层数组的直接子项深度必为 1。
                    _inElement = true;
                    _elementStart = _buffer.Start + (int)reader.TokenStartIndex;

                    if (token != JsonTokenType.StartObject && token != JsonTokenType.StartArray)
                    {
                        // 标量元素：一个 token 就是一帧。
                        return CompleteElement(reader);
                    }

                    continue;
                }

                if (depth == 1 && (token == JsonTokenType.EndObject || token == JsonTokenType.EndArray))
                {
                    // 收尾 token 与开括号同深度。
                    return CompleteElement(reader);
                }
            }
        }
        catch (JsonException ex)
        {
            throw AxiosException.StreamMalformed(_config, _request, "JSON array stream is not valid JSON", ex);
        }

        // 缓冲区读完：先结算已消费字节再补读。
        _readerState = reader.CurrentState;
        var consumed = (int)reader.BytesConsumed;
        SpillConsumed(consumed);
        _buffer.Advance(consumed);
        return null;
    }

    /// <summary>元素收尾：切出它的字节区间并重置元素状态；跨块元素先拼上 spill。</summary>
    /// <param name="reader">已读到收尾 token 的读取器。</param>
    private FramePayload CompleteElement(Utf8JsonReader reader)
    {
        // BytesConsumed 相对本次窗口起点。
        var end = _buffer.Start + (int)reader.BytesConsumed;
        var element = _elementStart >= 0
            ? ElementInBuffer(end)
            : ElementSpilled(end);

        _readerState = reader.CurrentState;
        _buffer.MoveStart(end);
        _inElement = false;
        _elementStart = -1;
        return element;
    }

    /// <summary>元素完全落在当前缓冲区：按原始字节（或文本）交付。</summary>
    /// <param name="end">元素收尾后的窗口游标。</param>
    private FramePayload ElementInBuffer(int end)
        => _byteFrames
            ? FramePayload.FromBytes(_buffer.Memory(_elementStart, end - _elementStart))
            : FramePayload.FromText(_buffer.Decode(_elementStart, end - _elementStart, _encoding));

    /// <summary>把「当前元素已消费、但还没交付」的字节搬进 spill 缓冲区，随后缓冲区可以安全压缩。</summary>
    /// <param name="consumed">本次窗口已消费的字节数。</param>
    private void SpillConsumed(int consumed)
    {
        if (!_inElement)
        {
            return;
        }

        var from = _elementStart >= 0 ? _elementStart : _buffer.Start;
        var to = _buffer.Start + consumed;
        if (to <= from)
        {
            // 元素起点还没被消费：起点留在缓冲区里。
            return;
        }

        AppendSpill(from, to - from);
        _elementStart = -1;
    }

    /// <summary>跨块元素收尾：把缓冲区里的尾巴接上 spill，一次交付。</summary>
    /// <param name="end">元素收尾后的窗口游标。</param>
    private FramePayload ElementSpilled(int end)
    {
        AppendSpill(_buffer.Start, end - _buffer.Start);

        var length = _elementSpilled;
        _elementSpilled = 0;

        // 超过基础容量的溢写缓冲直接还掉。
        var buffer = _elementSpill;
        if (buffer is not null && buffer.Length > MaxRetainedSpill)
        {
            _elementSpill = null;
        }

        if (length == 0 || buffer is null)
        {
            return FramePayload.FromText(string.Empty);
        }

        return _byteFrames
            ? FramePayload.FromBytes(buffer.AsMemory(0, length))
            : FramePayload.FromText(ResponseTextDecoder.DecodeResolved(buffer.AsSpan(0, length), _encoding));
    }

    /// <summary>缓冲区 [start, end) 区间是否只有 JSON 空白（空格 / 制表 / CR / LF）。</summary>
    /// <param name="start">起点下标。</param>
    /// <param name="end">终点下标（开区间）。</param>
    private bool IsWhitespace(int start, int end)
    {
        var span = _buffer.Span(start, end - start);
        for (var i = 0; i < span.Length; i++)
        {
            var b = span[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把缓冲区 <paramref name="offset"/> 起的 <paramref name="length"/> 个字节追加到 spill。</summary>
    /// <param name="offset">源起点下标。</param>
    /// <param name="length">字节数。</param>
    private void AppendSpill(int offset, int length)
    {
        if (length <= 0)
        {
            return;
        }

        var total = _elementSpilled + length;
        if (total > _maxFrameBytes)
        {
            // 单个元素必须能整块交给序列化器。
            throw AxiosException.StreamElementTooLarge(_config, _request, FormatName, _maxFrameBytes);
        }

        if (_elementSpill is null)
        {
            _elementSpill = new byte[Math.Min(Math.Max(SegmentedBuffer.ChunkSize, total), _maxFrameBytes)];
        }
        else if (total > _elementSpill.Length)
        {
            var grown = new byte[Math.Min(Math.Max(_elementSpill.Length * 2, total), _maxFrameBytes)];
            Buffer.BlockCopy(_elementSpill, 0, grown, 0, _elementSpilled);
            _elementSpill = grown;
        }

        _buffer.CopyTo(offset, length, _elementSpill, _elementSpilled);
        _elementSpilled = total;
    }

    /// <summary>跳过前导 BOM：UTF-8 BOM 必须剥掉，UTF-16 / UTF-32 的 BOM 说明正文不是 UTF-8 字节，直接报错。</summary>
    private void SkipBom()
    {
        var window = _buffer.Window;
        if (BomDetection.HasUtf8Bom(window))
        {
            _buffer.Advance(3);
            return;
        }

        if (ResponseTextDecoder.DetectFromBom(window) is { } bom && bom.CodePage != Encoding.UTF8.CodePage)
        {
            throw AxiosException.StreamMalformed(
                _config,
                _request,
                $"JSON array stream is encoded as '{bom.WebName}' (only UTF-8 can be token-framed)",
                new NotSupportedException("JSON array framing requires UTF-8"));
        }
    }

    /// <summary>补读一块；缓冲压缩后把 <c>_elementStart</c> 一并前移。</summary>
    /// <param name="token">枚举令牌。</param>
    private async ValueTask FillAsync(CancellationToken token)
    {
        await _buffer.FillAsync(token).ConfigureAwait(false);

        if (_buffer.CompactionShift > 0 && _elementStart >= 0)
        {
            _elementStart -= _buffer.CompactionShift;
        }
    }

    /// <summary>构造「数组流被截断」的异常。</summary>
    /// <param name="message">诊断文案。</param>
    private AxiosException Malformed(string message)
        => AxiosException.StreamMalformed(_config, _request, message, new JsonException("unexpected end of the JSON array stream"));
}
