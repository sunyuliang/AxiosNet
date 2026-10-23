using System.Text;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 把响应流按行切成帧、逐帧反序列化成 <typeparamref name="T"/>（NDJSON / SSE）。
/// <para>
/// <b>分帧在字节层做</b>：先找 <c>0x0A</c> / <c>0x0D</c> 再解码整行。反过来（先把字节流解码成文本再切行）会在
/// 块边界撞上半个 UTF-8 序列，解出不可恢复的 U+FFFD —— 而 CR / LF 不会出现在多字节序列内部，
/// 按字节切分天然安全。
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

    private readonly Stream _stream;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage? _request;
    private readonly AxiosStreamFormat _format;
    private readonly string? _charSet;
    private readonly string? _mediaType;
    private readonly StringBuilder _frameData = new();

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
        var framingEncoding = config.ResponseEncoding ?? ResponseTextDecoder.ResolveFromCharset(charSet);
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

        _stream = stream;
        _config = config;
        _request = request;
        _format = format;
        _charSet = charSet;
        _mediaType = mediaType;
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
        while (true)
        {
            if (await TryReadLineAsync().ConfigureAwait(false) is not { } line)
            {
                // EOF：SSE 可能没写最后的空行，残留的 data 也要交付一次
                if (_format == AxiosStreamFormat.ServerSentEvents && _frameData.Length > 0)
                {
                    var tail = _frameData.ToString();
                    _frameData.Clear();
                    return IsDone(tail) ? null : tail;
                }

                return null;
            }

            if (_format == AxiosStreamFormat.NewlineDelimitedJson)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                return line;
            }

            // SSE：空行结束一帧
            if (line.Length == 0)
            {
                if (_frameData.Length == 0)
                {
                    // 心跳（`:` 注释）帧没有 data，跳过
                    continue;
                }

                var frame = _frameData.ToString();
                _frameData.Clear();
                return IsDone(frame) ? null : frame;
            }

            if (line[0] == ':')
            {
                // 注释 / 心跳
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            if (!field.Equals("data", StringComparison.Ordinal))
            {
                // event: / id: / retry: 等字段不进入数据流
                continue;
            }

            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (_frameData.Length > 0)
            {
                _frameData.Append('\n');
            }

            _frameData.Append(value);
            if (_frameData.Length > MaxFrameBytes)
            {
                throw AxiosException.StreamFrameTooLarge(_config, _request, "SSE", MaxFrameBytes);
            }
        }
    }

    private static bool IsDone(string frame)
        => string.Equals(frame, DoneSentinel, StringComparison.Ordinal);

    /// <summary>读取一行（不含行尾 CR / LF）；<c>null</c> 表示流已结束。</summary>
    private async ValueTask<string?> TryReadLineAsync()
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

                var line = Decode(_start, terminator - _start);
                _start = terminator + 1;

                // 裸 CR / 裸 LF 只前进 1 字节；CRLF 再吃掉 LF。
                if (_buffer[terminator] == (byte)'\r'
                    && _start < _end
                    && _buffer[_start] == (byte)'\n')
                {
                    _start++;
                }

                return line;
            }

            if (_eof)
            {
                if (_start >= _end)
                {
                    return null;
                }

                var tail = Decode(_start, _end - _start);
                _start = _end;
                return tail;
            }

            await FillAsync().ConfigureAwait(false);
        }
    }

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
        => length <= 0 ? string.Empty : ResponseTextDecoder.Decode(_buffer.AsSpan(offset, length), _charSet, _config.ResponseEncoding);

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

            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= MaxFrameBytes)
            {
                throw AxiosException.StreamFrameTooLarge(_config, _request, _format.ToString(), MaxFrameBytes);
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
            var serializer = AxiosContent.Get(_config, _mediaType);
            var value = serializer.Deserialize(payload, typeof(T), _config);

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
