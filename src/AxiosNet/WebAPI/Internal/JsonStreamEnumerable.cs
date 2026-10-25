using System.Text;
using System.Text.Json;
using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 把响应流切成帧、逐帧反序列化成 <typeparamref name="T"/>（NDJSON / SSE / JSON 数组）。
/// <para>
/// <b>分帧在字节层做</b>：NDJSON / SSE 先找 <c>0x0A</c> / <c>0x0D</c> 再解码整行。反过来（先把字节流解码成文本再切行）会在
/// 块边界撞上半个 UTF-8 序列，解出不可恢复的 U+FFFD —— 而 CR / LF 不会出现在多字节序列内部，
/// 按字节切分天然安全。
/// </para>
/// <para>
/// <b>JSON 数组</b>没有行分隔符，改由 <see cref="Utf8JsonReader"/> 增量定位「顶层元素」的字节区间：
/// 解析器自己跨过字符串里的 <c>,</c> / <c>]</c>、转义序列与嵌套结构，所以既不必先把整个数组读进内存，
/// 也不会被 <c>{"text":"a]b"}</c> 这类内容骗到。元素跨多次读取时，已消费的前缀溢写进单独的
/// spill 缓冲区（只在元素真的超过一次读大小时才分配），交付前再拼回去一次解码。
/// </para>
/// <para>
/// 没有直接用 <c>JsonSerializer.DeserializeAsyncEnumerable</c>：那条路自己反序列化，会绕开
/// <see cref="IContentSerializer"/>（XML / 第三方 JSON 引擎与自定义选项就全废了），也拿不到
/// 「8 MB 单元素上限」与统一的 <c>ERR_BAD_RESPONSE</c> 映射。这里只用 reader 找边界，元素仍交给序列化器。
/// </para>
/// <para>
/// 响应流只能读一次：<see cref="GetAsyncEnumerator"/> 只允许调用一次，重复枚举直接报错，
/// 而不是让调用方看到一个「已释放的流」的模糊错误。
/// </para>
/// </summary>
internal sealed class JsonStreamEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    private const int ReadChunk = 8 * 1024;
    private const int MaxFrameBytes = 8 * 1024 * 1024;
    private const string DoneSentinel = "[DONE]";
    private const string ArrayFormatName = "JSON array";

    private readonly Stream _stream;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage? _request;
    private readonly AxiosStreamFormat _format;

    /// <summary>
    /// 整个响应流固定的生效编码（取流时解析一次）：null = 未声明 charset 也未强制，按行 BOM 探测。
    /// JSON 数组模式恒为 UTF-8（分帧已按 UTF-8 解释过字节）。
    /// </summary>
    private readonly Encoding? _encoding;

    /// <summary>整个响应流固定的序列化器（媒体类型与配置都不变）：避免每帧重复查注册表 / 归一化媒体类型。</summary>
    private readonly IContentSerializer _serializer;

    private readonly StringBuilder _frameData = new();

    // —— JSON 数组模式的分帧状态（其余格式不用） ——

    /// <summary>跨多次读取时 <see cref="Utf8JsonReader"/> 的续读状态：缓冲区会压缩 / 换新，解析位置只能记在这里。</summary>
    private JsonReaderState _readerState;

    /// <summary>已处理过前导 BOM（只做一次；空正文也算处理过）。</summary>
    private bool _arrayHeaderChecked;

    /// <summary>已见到顶层 <c>[</c>。</summary>
    private bool _arrayStarted;

    /// <summary>已见到顶层 <c>]</c>：之后不再读流。</summary>
    private bool _arrayEnded;

    /// <summary>当前元素已开始、尚未收尾。</summary>
    private bool _inElement;

    /// <summary>当前元素在本缓冲区里的起点；<c>-1</c> 表示前缀已经搬进 <see cref="_elementSpill"/>。</summary>
    private int _elementStart = -1;

    /// <summary>跨块元素的已消费前缀（懒分配：元素不超过一次读大小时永远为 null）。</summary>
    private byte[]? _elementSpill;

    /// <summary><see cref="_elementSpill"/> 中的有效字节数。</summary>
    private int _elementSpilled;

    /// <summary>当前 SSE 帧已累积的<b>原始字节数</b>（分帧上限按字节判定，与 NDJSON 缓冲区口径一致）。</summary>
    private int _frameBytes;

    private byte[] _buffer = new byte[ReadChunk];
    private int _start;
    private int _end;
    private bool _eof;
    private bool _finished;
    private bool _disposed;
    private bool _enumeratorTaken;
    private CancellationToken _token;
    private T _current = default!;

    public JsonStreamEnumerable(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage? request,
        AxiosStreamFormat format,
        string? charSet,
        string? mediaType)
    {
        // 强制 ResponseEncoding 优先于 charset（与 Decode 的优先级一致），按最终生效的编码判断；
        // 只看 charSet 会漏掉「未声明 charset + 调用方强制 UTF-16」，那种配置同样无法按字节分帧。
        var framingEncoding = config.SerializationOrNull?.ResponseEncoding ?? ResponseTextDecoder.ResolveFromCharset(charSet);
        if (!ResponseTextDecoder.IsAsciiCompatible(framingEncoding))
        {
            // 分帧按字节找 CR / LF：UTF-16 / UTF-32 把换行编成多字节序列，切出来的「行」必然错位。
            // 与其让调用方拿到一串乱码帧，不如在取流这一步报出可定位的错误（同时释放响应流归还连接）。
            stream.Dispose();
            throw AxiosException.BadResponse(
                config,
                request,
                $"stream frames in charset '{framingEncoding!.WebName}' (only ASCII-compatible encodings can be byte-framed)",
                new NotSupportedException("stream framing requires an ASCII-compatible charset"));
        }

        if (format == AxiosStreamFormat.JsonArray
            && framingEncoding is not null
            && framingEncoding.CodePage != Encoding.UTF8.CodePage
            && framingEncoding.CodePage != Encoding.ASCII.CodePage)
        {
            // JSON 数组分帧交给 Utf8JsonReader，它只认 UTF-8 字节。gbk / latin1 之类「ASCII 兼容但非 UTF-8」
            // 的声明下，含非 ASCII 字符的 JSON 字节本身就不是合法 UTF-8，逐元素解析会炸成一串
            // JsonException（看起来像服务端发错数据，其实是编码声明与格式不匹配）。
            // 在取流这一步说清楚：JSON 只能按 UTF-8 分帧。
            stream.Dispose();
            throw AxiosException.StreamMalformed(
                config,
                request,
                $"JSON array frames in charset '{framingEncoding.WebName}' are not supported (only UTF-8 JSON can be token-framed)",
                new NotSupportedException("JSON array framing requires UTF-8"));
        }

        _stream = stream;
        _config = config;
        _request = request;
        _format = format;

        // framing 校验已经解析过「最终生效的编码」（强制 ResponseEncoding 优先于 charset），
        // 结果直接留给逐行解码复用：每行重跑 ResolveFromCharset 在非 UTF-8 声明时是一次编码表查找。
        // 数组模式固定 UTF-8：字节已经被 Utf8JsonReader 按 UTF-8 解释过，元素文本必须按同一套编码还原，
        // 声明 us-ascii 的响应里出现中文时也不会被解成 '?'。
        _encoding = format == AxiosStreamFormat.JsonArray ? Encoding.UTF8 : framingEncoding;

        // 序列化器对整个响应流固定：取流时解析一次。每帧解析会让 NDJSON / SSE 的
        // 高帧率场景（LLM token 流、行情推送）每帧白付一次媒体类型归一化 + 注册表查找。
        _serializer = AxiosContent.Get(config, mediaType);

        // 外层数组的方言必须与元素最终被反序列化时的选项一致：序列化器允许注释 / 尾逗号 / 更深嵌套，
        // 分帧却按默认方言先拒绝，只会让调用方看到「改 options 也无效」的怪现象。
        if (format == AxiosStreamFormat.JsonArray)
        {
            var jsonOptions = AxiosJson.GetOptions(config);
            var readerOptions = new JsonReaderOptions
            {
                AllowTrailingCommas = jsonOptions.AllowTrailingCommas,
                CommentHandling = jsonOptions.ReadCommentHandling,

                // 外层数组本身占掉一层：同一个元素在 reader 眼里比在序列化器眼里深 1。
                // 不补这一层的话，调用方把 MaxDepth 收紧到刚好够用时，元素能被序列化器接受、
                // 却先被分帧拒掉（0 = 用默认值，不能加）。
                MaxDepth = jsonOptions.MaxDepth == 0 ? 0 : jsonOptions.MaxDepth + 1
            };

            // 解析选项随 reader 状态一起携带（Utf8JsonReader 的增量构造只收 state）：
            // 首块与后续块必须用同一套方言，否则「续读时突然不认注释」这种差异只会在跨块边界上出现。
            _readerState = new JsonReaderState(readerOptions);
        }
    }

    public T Current => _current;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (_enumeratorTaken)
        {
            throw new InvalidOperationException(
                "流式响应只能枚举一次：Data 是单次读取的响应流，第二次枚举请重新发起请求。");
        }

        _enumeratorTaken = true;
        _token = cancellationToken;
        return this;
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(JsonStreamEnumerable<T>));
        }

        if (_finished)
        {
            return false;
        }

        while (true)
        {
            if (_token.IsCancellationRequested)
            {
                throw AxiosException.Canceled(_config, _request, new OperationCanceledException(_token));
            }

            string? payload;
            try
            {
                payload = await ReadPayloadAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (_token.IsCancellationRequested)
            {
                // 枚举令牌在挂起的 ReadAsync 里取消：这条路径的令牌是「调用方自己的」，
                // TimeoutStream 按契约原样抛出 OCE；在流式 API 层仍要满足
                // 「取消一律 ERR_CANCELED」，否则同一取消动作会因时机不同给出两种异常类型。
                throw AxiosException.Canceled(_config, _request, ex);
            }

            if (payload is null)
            {
                _finished = true;
                return false;
            }

            if (payload.Length == 0)
            {
                continue;
            }

            _current = Deserialize(payload);
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _finished = true;

        // 释放包装流 = 归还连接池租约 + 释放 HttpResponseMessage（ResponseStreamOwner 负责）
        return _stream.DisposeAsync();
    }

    /// <summary>读到下一个可交付的帧；<c>null</c> 表示流结束。</summary>
    private async ValueTask<string?> ReadPayloadAsync()
    {
        if (_format == AxiosStreamFormat.JsonArray)
        {
            return await ReadArrayElementAsync().ConfigureAwait(false);
        }

        while (true)
        {
            if (await TryReadLineAsync().ConfigureAwait(false) is not { } line)
            {
                // EOF：SSE 可能没写最后的空行，残留的 data 也要交付一次
                if (_format == AxiosStreamFormat.ServerSentEvents && _frameData.Length > 0)
                {
                    var tail = _frameData.ToString();
                    _frameData.Clear();
                    _frameBytes = 0;
                    return IsDone(tail) ? null : tail;
                }

                return null;
            }

            if (_format == AxiosStreamFormat.NewlineDelimitedJson)
            {
                if (line.Text.Length == 0)
                {
                    continue;
                }

                return line.Text;
            }

            // SSE：空行结束一帧
            if (line.Text.Length == 0)
            {
                if (_frameData.Length == 0)
                {
                    // 心跳（`:` 注释）帧没有 data，跳过
                    continue;
                }

                var frame = _frameData.ToString();
                _frameData.Clear();
                _frameBytes = 0;
                return IsDone(frame) ? null : frame;
            }

            var text = line.Text;
            if (text[0] == ':')
            {
                // 注释 / 心跳
                continue;
            }

            var colon = text.IndexOf(':');
            var field = colon < 0 ? text : text[..colon];
            if (!field.Equals("data", StringComparison.Ordinal))
            {
                // event: / id: / retry: 等字段不进入数据流
                continue;
            }

            var value = colon < 0 ? string.Empty : text[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (_frameData.Length > 0)
            {
                _frameData.Append('\n');
            }

            _frameData.Append(value);

            // 上限按字节数（NDJSON 的缓冲区口径）：按字符算的话 UTF-8 多字节帧最多可灌约
            // 4× 上限的源字节才触发，StringBuilder 的 UTF-16 存储还让内存峰值再翻倍。
            _frameBytes += line.ByteLength;
            if (_frameBytes > MaxFrameBytes)
            {
                throw AxiosException.StreamFrameTooLarge(_config, _request, "SSE", MaxFrameBytes);
            }
        }
    }

    private static bool IsDone(string frame)
        => string.Equals(frame, DoneSentinel, StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // JSON 数组：Utf8JsonReader 增量定位顶层元素
    // ------------------------------------------------------------------

    /// <summary>
    /// 读到数组里的下一个顶层元素；<c>null</c> 表示读到收尾的 <c>]</c>（或空正文）。
    /// <para>
    /// 每轮用「当前缓冲区 + 上次留下的 <see cref="JsonReaderState"/>」新建一个 reader：缓冲区会被压缩 / 换新，
    /// 解析位置只能靠 reader 状态延续。reader 只负责<b>找边界</b>（并顺带校验 JSON 合法性），
    /// 元素文本仍交给 <see cref="Deserialize"/> 走序列化器，自定义序列化器不受影响。
    /// </para>
    /// </summary>
    private async ValueTask<string?> ReadArrayElementAsync()
    {
        while (true)
        {
            if (_arrayEnded)
            {
                return null;
            }

            if (!_arrayHeaderChecked)
            {
                // BOM 判定最多需要 3 个字节：首块可能只到一半（单个 0xEF 会被 Utf8JsonReader 当成非法起始）。
                while (_end - _start < 3 && !_eof)
                {
                    await FillAsync().ConfigureAwait(false);
                }

                _arrayHeaderChecked = true;
                SkipArrayBom();
            }

            if (_eof && IsWhitespace(_start, _end))
            {
                // 数据在「元素边界 / 空白」上耗尽：要么正文本来就是空的，要么数组被截断。
                // 两种情况都必须在建 reader 之前判断 —— isFinalBlock: true 的空输入会让 Utf8JsonReader
                // 直接抛「input does not contain any JSON tokens」，把「被截断」说成「不是合法 JSON」，
                // 而这两个原因要查的地方完全不同。
                if (!_arrayStarted)
                {
                    // 正文为空 / 只有空白：没有数组可交付。与 NDJSON 空流同口径，不当作错误。
                    _arrayEnded = true;
                    return null;
                }

                throw AxiosException.StreamMalformed(
                    _config,
                    _request,
                    "JSON array stream ended before its closing ']'",
                    new JsonException("unexpected end of the JSON array stream"));
            }

            var reader = new Utf8JsonReader(
                _buffer.AsSpan(_start, _end - _start),
                _eof,
                _readerState);

            try
            {
                while (reader.Read())
                {
                    var token = reader.TokenType;
                    if (token == JsonTokenType.Comment)
                    {
                        // CommentHandling.Allow 时注释会作为 token 出现：它既不是元素，也不改变深度
                        continue;
                    }

                    var depth = reader.CurrentDepth;

                    if (!_arrayStarted)
                    {
                        if (token != JsonTokenType.StartArray || depth != 0)
                        {
                            // 顶层不是数组：`{"items":[...]}` 这种包装对象最容易踩到，直接点名期望什么
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
                        // 顶层数组收尾：立即结束，不等连接关闭，也不校验尾部空白 / 垃圾
                        _readerState = reader.CurrentState;
                        _start += (int)reader.BytesConsumed;
                        _arrayEnded = true;
                        return null;
                    }

                    if (!_inElement)
                    {
                        // 顶层数组的直接子项深度必为 1；更深的 token 只可能出现在元素内部（那时 _inElement 已置位）。
                        // TokenStartIndex 是相对本次窗口起点（_start）的偏移，缓冲区偏移要补上 _start。
                        _inElement = true;
                        _elementStart = _start + (int)reader.TokenStartIndex;

                        if (token != JsonTokenType.StartObject && token != JsonTokenType.StartArray)
                        {
                            // 标量元素（数字 / 字符串 / true / false / null）：一个 token 就是一帧
                            return CompleteElement(reader);
                        }

                        continue;
                    }

                    if (depth == 1 && (token == JsonTokenType.EndObject || token == JsonTokenType.EndArray))
                    {
                        // 容器的收尾 token 与它的开括号同深度：回到 1 就说明元素本身结束了
                        return CompleteElement(reader);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw AxiosException.StreamMalformed(
                    _config,
                    _request,
                    "JSON array stream is not valid JSON",
                    ex);
            }

            // 缓冲区读完了（isFinalBlock: false 时这是常态）：先把已消费的字节结算掉，再补一块。
            // 顺序不能反：spill 复制的是「旧缓冲区 + 旧偏移」，压缩之后就对不上了。
            _readerState = reader.CurrentState;
            var consumed = (int)reader.BytesConsumed;
            SpillConsumed(consumed);
            _start += consumed;

            if (_eof)
            {
                // 兜底闸口：_eof 之后绝不能回到 FillAsync —— 那会对着已结束的流反复读 0 字节（死循环）。
                // 正常路径上「空正文」与「被截断」都在建 reader 之前就判掉了，这里只防 STJ 行为差异。
                throw AxiosException.StreamMalformed(
                    _config,
                    _request,
                    _arrayStarted
                        ? "JSON array stream ended before its closing ']'"
                        : "JSON array stream ended before its opening '['",
                    new JsonException("unexpected end of the JSON array stream"));
            }

            await FillAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 元素收尾：切出它在字节流里的区间并解码，重置元素状态。
    /// <para>
    /// 快路径（元素完整落在当前缓冲区）直接按偏移解码，零拷贝；慢路径（跨了多次读取）把尾巴拼到 spill 上
    /// 再一次解码 —— 拼接而非分段解码，是为了不必担心多字节字符落在两段之间（虽然 token 边界天然对齐）。
    /// </para>
    /// </summary>
    private string CompleteElement(Utf8JsonReader reader)
    {
        // BytesConsumed 是相对本次窗口起点 _start 的偏移
        var end = _start + (int)reader.BytesConsumed;
        var element = _elementStart >= 0
            ? Decode(_elementStart, end - _elementStart)
            : DecodeSpilled(end);

        _readerState = reader.CurrentState;
        _start = end;
        _inElement = false;
        _elementStart = -1;
        return element;
    }

    /// <summary>把「当前元素已消费、但还没交付」的字节搬进 spill 缓冲区，随后缓冲区可以安全压缩。</summary>
    private void SpillConsumed(int consumed)
    {
        if (!_inElement)
        {
            return;
        }

        var from = _elementStart >= 0 ? _elementStart : _start;
        var to = _start + consumed;
        if (to <= from)
        {
            // 元素起点还没被消费（例如超大字符串 token 尚未闭合）：起点留在缓冲区里，等下一轮
            return;
        }

        AppendSpill(from, to - from);
        _elementStart = -1;
    }

    /// <summary>跨块元素收尾：把缓冲区里的尾巴接上 spill，一次解码。</summary>
    private string DecodeSpilled(int end)
    {
        AppendSpill(_start, end - _start);

        var length = _elementSpilled;
        _elementSpilled = 0;
        return length == 0
            ? string.Empty
            : ResponseTextDecoder.DecodeResolved(_elementSpill!.AsSpan(0, length), _encoding);
    }

    /// <summary>缓冲区 [start, end) 区间是否只有 JSON 空白（空格 / 制表 / CR / LF）。</summary>
    private bool IsWhitespace(int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var b = _buffer[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把缓冲区 <paramref name="offset"/> 起的 <paramref name="length"/> 个字节追加到 spill。</summary>
    private void AppendSpill(int offset, int length)
    {
        if (length <= 0)
        {
            return;
        }

        var total = _elementSpilled + length;
        if (total > MaxFrameBytes)
        {
            // 单个元素必须能整块交给序列化器，否则内存占用由服务端决定
            throw AxiosException.StreamElementTooLarge(_config, _request, ArrayFormatName, MaxFrameBytes);
        }

        if (_elementSpill is null)
        {
            _elementSpill = new byte[Math.Min(Math.Max(ReadChunk, total), MaxFrameBytes)];
        }
        else if (total > _elementSpill.Length)
        {
            var grown = new byte[Math.Min(Math.Max(_elementSpill.Length * 2, total), MaxFrameBytes)];
            Buffer.BlockCopy(_elementSpill, 0, grown, 0, _elementSpilled);
            _elementSpill = grown;
        }

        Buffer.BlockCopy(_buffer, offset, _elementSpill, _elementSpilled, length);
        _elementSpilled = total;
    }

    /// <summary>
    /// 跳过前导 BOM。UTF-8 BOM 必须剥掉（Utf8JsonReader 会把 0xEF 当成非法起始）；
    /// UTF-16 / UTF-32 的 BOM 说明正文根本不是 UTF-8 字节，报错而不是让解析器逐 token 崩。
    /// </summary>
    private void SkipArrayBom()
    {
        var span = _buffer.AsSpan(_start, _end - _start);
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            _start += 3;
            return;
        }

        if (ResponseTextDecoder.DetectFromBom(span) is { } bom && bom.CodePage != Encoding.UTF8.CodePage)
        {
            throw AxiosException.StreamMalformed(
                _config,
                _request,
                $"JSON array stream is encoded as '{bom.WebName}' (only UTF-8 can be token-framed)",
                new NotSupportedException("JSON array framing requires UTF-8"));
        }
    }

    /// <summary>读取一行（不含行尾 CR / LF）及其原始字节数；<c>null</c> 表示流已结束。</summary>
    private async ValueTask<Line?> TryReadLineAsync()
    {
        while (true)
        {
            var terminator = IndexOfLineTerminator();
            if (terminator >= 0)
            {
                // CRLF 的 CR 恰好落在缓冲区末尾：再读一块确认后随的 LF，
                // 否则会把 LF 当成下一行的空行（SSE 会多交付一个空帧）。
                if (_buffer[terminator] == (byte)'\r' && terminator == _end - 1 && !_eof)
                {
                    await FillAsync().ConfigureAwait(false);
                    continue;
                }

                var byteLength = terminator - _start;
                var line = Decode(_start, byteLength);
                _start = terminator + 1;

                // 裸 CR / 裸 LF 只前进 1 字节；CRLF 再吃掉 LF。
                if (_buffer[terminator] == (byte)'\r'
                    && _start < _end
                    && _buffer[_start] == (byte)'\n')
                {
                    _start++;
                }

                return new Line(line, byteLength);
            }

            if (_eof)
            {
                if (_start >= _end)
                {
                    return null;
                }

                var tailLength = _end - _start;
                var tail = Decode(_start, tailLength);
                _start = _end;
                return new Line(tail, tailLength);
            }

            await FillAsync().ConfigureAwait(false);
        }
    }

    /// <summary>一行文本 + 它在原始字节流里的长度（SSE 分帧上限按字节判定，与 NDJSON 缓冲区口径一致）。</summary>
    private readonly record struct Line(string Text, int ByteLength);

    /// <summary>
    /// 行尾判定：LF / CR / CRLF 都是合法行分隔符（WHATWG SSE 明确允许三种），
    /// 返回最早出现的 CR 或 LF 的下标，没有则返回 -1。
    /// </summary>
    private int IndexOfLineTerminator()
    {
        var length = _end - _start;
        if (length <= 0)
        {
            return -1;
        }

        var index = _buffer.AsSpan(_start, length).IndexOfAny((byte)'\r', (byte)'\n');
        return index < 0 ? -1 : _start + index;
    }

    private string Decode(int offset, int length)
        => length <= 0 ? string.Empty : ResponseTextDecoder.DecodeResolved(_buffer.AsSpan(offset, length), _encoding);

    /// <summary>压缩未消费字节后补读一块；缓冲区到达单帧上限时抛错而不是无限增长。</summary>
    private async ValueTask FillAsync()
    {
        if (_start > 0)
        {
            var remaining = _end - _start;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
            }

            // 数组模式下 _elementStart 是缓冲区绝对偏移：压缩会让它一起前移。
            // 目前「元素起点仍在本缓冲区」与「本轮需要补读」不会同时发生（要么元素已交付，
            // 要么前缀已经溢写出去），但那是推理出来的不变量；这里显式跟着搬，
            // 将来改动分帧循环时不会因为一个偏移没跟上而悄悄切错元素。
            if (_elementStart >= 0)
            {
                _elementStart -= _start;
            }

            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= MaxFrameBytes)
            {
                // 数组模式撑满缓冲只可能是「单个 token 太大」（元素由多个 token 组成时会在 AppendSpill 先拦下），
                // 措辞统一成「元素超限」：数组里的分隔符是 ','，说「缺少分帧符」会把人引向错误方向。
                throw _format == AxiosStreamFormat.JsonArray
                    ? AxiosException.StreamElementTooLarge(_config, _request, ArrayFormatName, MaxFrameBytes)
                    : AxiosException.StreamFrameTooLarge(_config, _request, _format.ToString(), MaxFrameBytes);
            }

            var grown = new byte[Math.Min(_buffer.Length * 2, MaxFrameBytes)];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _end);
            _buffer = grown;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), _token).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
        }
        else
        {
            _end += read;
        }
    }

    private T Deserialize(string payload)
    {
        try
        {
            var value = _serializer.Deserialize(payload, typeof(T), _config);

            // null 是合法帧（JSON 的 null）；非 null 却不是 T 说明序列化器与目标类型不匹配，
            // 静默返回 default! 会把编程错误伪装成一帧空数据。
            if (value is null)
            {
                return default!;
            }

            if (value is T typed)
            {
                return typed;
            }

            throw new InvalidCastException(
                $"stream item expected '{typeof(T)}' but the serializer returned '{value.GetType()}'");
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.BadResponse(_config, _request, $"stream item ({typeof(T).Name})", ex);
        }
    }
}
