using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 把 <see cref="AxiosConfig.Data"/> 转成 <see cref="HttpContent"/>：正文格式由 <see cref="AxiosConfig.ContentType"/> 决定，
/// 未显式设置时采用生效序列化器自报的 <see cref="IContentSerializer.MediaType"/>。
/// <list type="bullet">
/// <item>string / byte[] / Memory / ArraySegment / Stream / HttpContent：原样发送，只应用 Content-Type。</item>
/// <item>IAsyncEnumerable&lt;byte[]&gt; / IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;：按块流式发送，长度未知。</item>
/// <item>基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）：直接取文本，不经过序列化器。</item>
/// <item>结构化数据：json → 序列化器；form-urlencoded → <see cref="FormBodyEncoder"/>；multipart → <see cref="MultipartBodyBuilder"/>。</item>
/// </list>
/// </summary>
internal static class RequestBodyFactory
{
    private const string MultipartMediaType = AxiosContentType.MultipartFormData;

    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 正文编码：默认 UTF-8；charset 只在显式设置 RequestEncoding 时写入。
        var requestEncoding = config.SerializationOrNull?.RequestEncoding;
        var encoding = requestEncoding ?? Encoding.UTF8;
        var appendCharset = requestEncoding is not null;
        var mediaType = config.ContentType;

        // 显式声明的 charset 优先于 RequestEncoding，只对文本正文生效；两者矛盾时直接拦下。
        if (data is not null
            && EncodesText(data)
            && RequestContentType.GetCharsetEncoding(mediaType) is { } declared)
        {
            if (requestEncoding is { } requested && declared.CodePage != requested.CodePage)
            {
                throw AxiosException.InvalidOption(
                    $"ContentType 声明的 charset（{declared.WebName}）与 RequestEncoding（{requested.WebName}）不一致："
                    + "两者描述的是同一个请求体的字符编码，请只保留一处（或改成一致）。", config);
            }

            encoding = declared;
        }

        // 请求头里的 Content-Type 若带 charset，必须与正文实际编码一致（multipart 不参与）。
        if (data is not null
            && EncodesText(data)
            && !MediaTypes.Is(mediaType, MultipartMediaType)
            && config.HasHeaders
            && config.Headers.Get("Content-Type") is { Length: > 0 } headerContentType
            && RequestContentType.GetCharsetEncoding(headerContentType) is { } headerCharset
            && headerCharset.CodePage != encoding.CodePage)
        {
            throw AxiosException.InvalidOption(
                $"请求头里的 Content-Type 声明 charset={headerCharset.WebName}，但正文实际按 {encoding.WebName} 编码："
                + "两者不一致会让服务端按头解出乱码。请改用 AxiosConfig.ContentType 声明编码"
                + "（它的 charset 会驱动正文编码），或去掉请求头里的 charset。", config);
        }

        // multipart + 原始内容：没有 boundary 无法解析。
        if (data is not null and not HttpContent
            && IsRawContent(data)
            && MediaTypes.Is(mediaType, MultipartMediaType)
            && RequestContentType.ExtractBoundary(mediaType) is null)
        {
            throw AxiosException.InvalidOption(
                "Content-Type 为 multipart/form-data 时，data 不能是原始内容"
                + "（string / byte[] / Memory / Stream / IAsyncEnumerable）：请传入对象或字典由库组装 multipart 字段，"
                + "或用 HttpContent（如 MultipartFormDataContent）承载预序列化的 multipart 正文。", config);
        }

        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 已构造好的内容原样透传；仅当内容没有 Content-Type 且用户显式配置过时才补头部。
                if (config.ContentTypeSet && existing.Headers.ContentType is null)
                {
                    RequestContentType.Set(existing, mediaType, config);
                }

                // multipart 没有 boundary 服务端必然解析失败，这里拦下。
                if (existing.Headers.ContentType?.MediaType is { } existingMediaType
                    && existingMediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                    && !RequestContentType.HasMultipartBoundary(existing.Headers.ContentType))
                {
                    throw AxiosException.InvalidOption(
                        "Content-Type 为 multipart 却没有 boundary：data 传入的 HttpContent 必须自带合法边界"
                        + "（如 MultipartFormDataContent），或把对象/字典交给库组装字段。", config);
                }

                return existing;

            case string text:
                return CreateText(text, mediaType, encoding, appendCharset, config);

            case byte[] bytes:
                return CreateBinary(bytes, mediaType, config);

            case ReadOnlyMemory<byte> memory:
                return CreateBinary(memory, mediaType, config);

            case Memory<byte> memory:
                return CreateBinary(memory, mediaType, config);

            case ArraySegment<byte> segment:
                return CreateBinary(segment.AsMemory(), mediaType, config);

            case Stream stream:
                var streamContent = new StreamContent(stream);
                RequestContentType.Set(streamContent, mediaType, config);
                return streamContent;

            // 流式正文：按块写出、不缓冲；长度未知，传输层走 chunked / DATA 帧。
            case IAsyncEnumerable<byte[]> chunks:
                return CreateStreaming(AsyncEnumerableBodyContent.FromByteChunks(chunks), mediaType, config);

            case IAsyncEnumerable<ReadOnlyMemory<byte>> chunks:
                return CreateStreaming(AsyncEnumerableBodyContent.FromMemoryChunks(chunks), mediaType, config);
        }

        // 其余 IAsyncEnumerable<T> 不能当正文，直接拦下。
        if (ReflectionCache.IsAsyncEnumerable(data.GetType()))
        {
            throw AxiosException.InvalidOption(
                $"data 是 {data.GetType().Name}（IAsyncEnumerable），但请求体只支持按字节块流式发送："
                + "请改用 IAsyncEnumerable<byte[]> / IAsyncEnumerable<ReadOnlyMemory<byte>>。"
                + "要发送 JSON 数组或文本流，请先把内容写成 Stream 再传入；"
                + "PipeReader 请用 pipe.Reader.AsStream() 包装后按 Stream 传入。", config);
        }

        return CreateStructured(data, config, mediaType, encoding, appendCharset);
    }

    /// <summary>流式正文只应用 Content-Type；长度与编码由内容自身决定。</summary>
    private static HttpContent CreateStreaming(HttpContent content, string mediaType, AxiosConfig config)
    {
        RequestContentType.Set(content, mediaType, config);
        return content;
    }

    private static HttpContent CreateText(string text, string mediaType, Encoding encoding, bool appendCharset, AxiosConfig config)
    {
        // 用 ByteArrayContent 保证携带 Content-Length。
        var content = new ByteArrayContent(encoding.GetBytes(text));
        RequestContentType.Set(content, RequestContentType.Compose(mediaType, encoding, appendCharset), config);
        return content;
    }

    private static HttpContent CreateBinary(byte[] bytes, string mediaType, AxiosConfig config)
    {
        var content = new ByteArrayContent(bytes);
        RequestContentType.Set(content, mediaType, config);
        return content;
    }

    /// <summary>零拷贝发送 <see cref="ReadOnlyMemory{T}"/>。</summary>
    private static HttpContent CreateBinary(ReadOnlyMemory<byte> memory, string mediaType, AxiosConfig config)
    {
        var content = new ReadOnlyMemoryContent(memory);
        RequestContentType.Set(content, mediaType, config);
        return content;
    }

    private static HttpContent CreateStructured(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        if (MediaTypes.Is(mediaType, AxiosConfig.FormUrlEncodedContentType))
        {
            return FormBodyEncoder.Create(data, config, mediaType, encoding, appendCharset);
        }

        if (MediaTypes.Is(mediaType, MultipartMediaType))
        {
            return MultipartBodyBuilder.Create(data, mediaType, encoding, config);
        }

        var type = data.GetType();

        // 未显式设置 ContentType 时采用序列化器自报的媒体类型（标量同样适用）。
        var serializer = AxiosContent.Get(config, mediaType);
        if (!config.ContentTypeSet && serializer.MediaType is { } declared && !string.IsNullOrWhiteSpace(declared))
        {
            mediaType = declared.Trim();
        }

        // 基础类型不经过序列化器，直接取文本。
        if (BodyFields.IsScalar(type))
        {
            var scalar = CreateText(BodyFields.ScalarBodyText(data), mediaType, encoding, appendCharset, config);
            ValidateDeclaredCharset(scalar, config, encoding);
            return scalar;
        }

        // 二进制格式的请求出口：非 null 表示该格式以字节为正文，原样发送。
        if (serializer.SerializeToBytes(data, type, config) is { } bytes)
        {
            return CreateBinary(bytes, mediaType, config);
        }

        // Serialize 可能往 config.Headers 写 Content-Type（含 charset）：产出之后再校验
        var text = CreateText(serializer.Serialize(data, type, config), mediaType, encoding, appendCharset, config);
        ValidateDeclaredCharset(text, config, encoding);
        return text;
    }

    /// <summary>
    /// 正文产出后校验最终 Content-Type 的 charset 与正文编码一致（请求头优先）。
    /// </summary>
    private static void ValidateDeclaredCharset(HttpContent content, AxiosConfig config, Encoding bodyEncoding)
    {
        var finalType = config.HasHeaders && config.Headers.Get("Content-Type") is { Length: > 0 } headerType
            ? headerType
            : content.Headers.ContentType?.ToString();

        if (finalType is { Length: > 0 }
            && RequestContentType.GetCharsetEncoding(finalType) is { } declared
            && declared.CodePage != bodyEncoding.CodePage)
        {
            throw AxiosException.InvalidOption(
                $"Content-Type 声明 charset={declared.WebName}，但请求体实际按 {bodyEncoding.WebName} 编码："
                + "两者不一致会让服务端按头解出乱码。请让序列器自报 / 写入与正文一致的 charset，"
                + "或改用 AxiosConfig.ContentType（它的 charset 会驱动正文编码）与 Serialization.RequestEncoding。", config);
        }
    }

    /// <summary>「内容原样发送」的类型：不经过任何展平 / 序列化。</summary>
    private static bool IsRawContent(object data)
        => data is string
            or byte[]
            or ReadOnlyMemory<byte>
            or Memory<byte>
            or ArraySegment<byte>
            or Stream
            or IAsyncEnumerable<byte[]>
            or IAsyncEnumerable<ReadOnlyMemory<byte>>;

    /// <summary>正文是否由本库按 <see cref="Encoding"/> 编成文本（string 与结构化数据）。</summary>
    private static bool EncodesText(object data)
        => data is string
            || (data is not HttpContent
                && data is not byte[]
                && data is not ReadOnlyMemory<byte>
                && data is not Memory<byte>
                && data is not ArraySegment<byte>
                && data is not Stream
                && data is not IAsyncEnumerable<byte[]>
                && data is not IAsyncEnumerable<ReadOnlyMemory<byte>>);
}
