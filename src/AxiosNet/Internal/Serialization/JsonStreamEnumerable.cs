using System.Text;
using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet.Internal.Serialization;

/// <summary>把响应流切成帧、逐帧反序列化成 <typeparamref name="T"/>（NDJSON / SSE / JSON 数组）。</summary>
/// <remarks>
/// 本类只管枚举生命周期、取消与反序列化；「怎么切」交给 <see cref="IFrameReader"/>（行协议 / JSON 数组各一个实现），
/// 「怎么读」交给 <see cref="SegmentedBuffer"/>。单帧上限默认 8 MB（<see cref="AxiosConfig.LimitsSettings.MaxStreamFrameBytes"/>）；
/// 响应流只能读一次，重复枚举报错。
/// </remarks>
internal sealed class JsonStreamEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    private const string ArrayFormatName = "JSON array";

    private readonly Stream _stream;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage? _request;

    /// <summary>整个响应流固定的生效编码（取流时解析一次）：<c>null</c> = 未声明也未强制。JSON 数组模式恒为 UTF-8。</summary>
    private readonly Encoding? _encoding;

    /// <summary>整个响应流固定的序列化器（取流时解析一次）。</summary>
    private readonly IContentSerializer _serializer;

    private readonly IFrameReader _reader;

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
        // 强制 ResponseEncoding 优先于 charset。
        var framingEncoding = config.SerializationOrNull?.TextEncoding ?? ResponseTextDecoder.ResolveFromCharset(charSet);
        if (!ResponseTextDecoder.IsAsciiCompatible(framingEncoding))
        {
            // 非 ASCII 兼容编码无法按字节分帧。
            stream.Dispose();
            throw AxiosException.BadResponse(config, request, $"stream frames in charset '{framingEncoding!.WebName}' (only ASCII-compatible encodings can be byte-framed)", new NotSupportedException("stream framing requires an ASCII-compatible charset"));
        }

        if (format == AxiosStreamFormat.JsonArray
            && framingEncoding is not null
            && framingEncoding.CodePage != Encoding.UTF8.CodePage
            && framingEncoding.CodePage != Encoding.ASCII.CodePage)
        {
            // JSON 数组分帧只认 UTF-8 字节。
            stream.Dispose();
            throw AxiosException.StreamMalformed(config, request, $"JSON array frames in charset '{framingEncoding.WebName}' are not supported (only UTF-8 JSON can be token-framed)", new NotSupportedException("JSON array framing requires UTF-8"));
        }

        _stream = stream;
        _config = config;
        _request = request;

        // 单帧上限随配置；null = 默认 8 MB。
        var maxFrameBytes = config.LimitsOrNull?.MaxStreamFrameBytes ?? AxiosConfig.DefaultStreamFrameBytes;

        _encoding = format == AxiosStreamFormat.JsonArray ? Encoding.UTF8 : framingEncoding;

        _serializer = AxiosContent.Get(config, mediaType);

        var buffer = new SegmentedBuffer(stream, maxFrameBytes, limit => format == AxiosStreamFormat.JsonArray
            ? AxiosException.StreamElementTooLarge(config, request, ArrayFormatName, limit)
            : AxiosException.StreamFrameTooLarge(config, request, format.ToString(), limit));

        _reader = format == AxiosStreamFormat.JsonArray
            ? new JsonArrayFrameReader(
                buffer,
                config,
                request,
                _encoding,

                // 声明非 UTF-8 的数组退回文本帧。
                byteFrames: framingEncoding is null || framingEncoding.CodePage == Encoding.UTF8.CodePage,

                maxFrameBytes,
                OuterArrayOptions(config))
            : new LineFrameReader(buffer, config, request, format, maxFrameBytes);
    }

    public T Current => _current;

    // 只允许枚举一次。
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (_enumeratorTaken)
        {
            throw new InvalidOperationException("流式响应只能枚举一次：Data 是单次读取的响应流，第二次枚举请重新发起请求。");
        }

        _enumeratorTaken = true;
        _token = cancellationToken;
        return this;
    }

    // 推进一帧；流结束或已释放时返回 false。
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

            FramePayload? payload;
            try
            {
                payload = await _reader.ReadAsync(_token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (_token.IsCancellationRequested)
            {
                // 挂起的 ReadAsync 被枚举令牌取消：统一映射成 ERR_CANCELED。
                throw AxiosException.Canceled(_config, _request, ex);
            }

            if (payload is not { } frame)
            {
                _finished = true;
                return false;
            }

            if (frame.Length == 0)
            {
                continue;
            }

            _current = Deserialize(frame);
            return true;
        }
    }

    // 释放包装流（连带归还连接池租约）；重复调用无副作用。
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _finished = true;

        // 释放包装流即归还连接池租约并释放 HttpResponseMessage。
        return _stream.DisposeAsync();
    }

    /// <summary>外层数组的 JSON 方言：与元素反序列化的选项保持一致，深度 +1。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    private static JsonReaderOptions OuterArrayOptions(AxiosConfig config)
    {
        var jsonOptions = AxiosJson.GetOptions(config);
        return new JsonReaderOptions
        {
            AllowTrailingCommas = jsonOptions.AllowTrailingCommas,
            CommentHandling = jsonOptions.ReadCommentHandling,

            // 外层数组占掉一层，MaxDepth 要 +1（0 = 默认值，不能加）。
            MaxDepth = jsonOptions.MaxDepth == 0 ? 0 : jsonOptions.MaxDepth + 1
        };
    }

    // 一帧 → T：优先字节入口，非 UTF-8 或字节非法时回退文本入口。
    private T Deserialize(in FramePayload payload)
    {
        try
        {
            // 字节入口省掉 UTF-16 往返；非法 UTF-8 也回退文本入口（坏字节会留到取值时才炸）。
            var text = payload.Text;
            if (text is null && !ResponseTextDecoder.IsValidUtf8(payload.Bytes.Span))
            {
                text = ResponseTextDecoder.DecodeResolved(payload.Bytes.Span, _encoding);
            }

            var value = text is null
                ? _serializer.Deserialize(payload.Bytes, typeof(T), _config)
                : _serializer.Deserialize(text, typeof(T), _config);

            // 非 null 却不是 T 说明序列化器与目标类型不匹配。
            if (value is null)
            {
                return default!;
            }

            if (value is T typed)
            {
                return typed;
            }

            throw new InvalidCastException($"stream item expected '{typeof(T)}' but the serializer returned '{value.GetType()}'");
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.BadResponse(_config, _request, $"stream item ({typeof(T).Name})", ex);
        }
    }
}
