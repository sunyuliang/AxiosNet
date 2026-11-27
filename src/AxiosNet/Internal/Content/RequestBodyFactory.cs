using AxiosNet.Metadata;
using AxiosNet.Serialization;

namespace AxiosNet.Internal.Content;

/// <summary>按 <see cref="AxiosConfig.Body"/> 的形态与生效 Content-Type 产出请求正文（没有正文返回 <c>null</c>）。</summary>
/// <remarks>原始内容（string / byte[] / Memory / Stream / IAsyncEnumerable）原样发送；<see cref="HttpContent"/> 透传；结构化数据按 json / form-urlencoded / multipart 分派；基础类型直接取文本。</remarks>
internal static class RequestBodyFactory
{
    // multipart 表单的媒体类型；boundary 由 builder 追加，这里不带参数。
    private const string MultipartMediaType = AxiosContentType.MultipartFormData;

    /// <summary>按 data 的实际形态与生效 Content-Type 产出请求正文；<c>null</c> 表示没有正文。</summary>
    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 正文编码：默认 UTF-8；charset 只在显式设置 RequestEncoding 时写入。
        var requestEncoding = config.SerializationOrNull?.TextEncoding;
        var encoding = requestEncoding ?? Encoding.UTF8;
        var appendCharset = requestEncoding is not null;
        var mediaType = config.ContentType;

        // 显式声明的 charset 优先于 RequestEncoding，只对文本正文生效；矛盾时拦下。
        if (data is not null
            && EncodesText(data)
            && RequestContentType.ResolveCharset(mediaType, config) is { } declared)
        {
            if (requestEncoding is { } requested && declared.CodePage != requested.CodePage)
            {
                throw AxiosException.InvalidOption(
                    $"ContentType 声明的 charset（{declared.WebName}）与 RequestEncoding（{requested.WebName}）不一致："
                    + "两者描述的是同一个请求体的字符编码，请只保留一处（或改成一致）。", config);
            }

            encoding = declared;
        }

        // 请求头里的 Content-Type 带 charset 时必须与正文实际编码一致（multipart 不参与）。
        if (data is not null
            && EncodesText(data)
            && !MediaTypes.Is(mediaType, MultipartMediaType)
            && config.HasHeaders
            && config.Headers.Get("Content-Type") is { Length: > 0 } headerContentType
            && RequestContentType.ResolveCharset(headerContentType, config) is { } headerCharset
            && headerCharset.CodePage != encoding.CodePage)
        {
            throw AxiosException.InvalidOption(
                $"请求头里的 Content-Type 声明 charset={headerCharset.WebName}，但正文实际按 {encoding.WebName} 编码："
                + "两者不一致会让服务端按头解出乱码。请改用 AxiosConfig.ContentType 声明编码"
                + "（它的 charset 会驱动正文编码），或去掉请求头里的 charset。", config);
        }

        // 请求体压缩只覆盖库自己缓冲出来的正文。
        if (data is not null && Compression.IsEnabled(config))
        {
            EnsureCompressible(data, mediaType, config);
        }

        // multipart + 原始内容：库没法替调用方补分隔行、Content-Disposition 与尾分隔符，
        // 带不带 boundary 都只会发出一条服务端解析不了的正文，因此一律在本地拒绝。
        if (data is not null and not HttpContent
            && IsRawContent(data)
            && MediaTypes.Is(mediaType, MultipartMediaType))
        {
            throw AxiosException.InvalidOption(
                "Content-Type 为 multipart/form-data 时，data 不能是原始内容"
                + "（string / byte[] / Memory / Stream / IAsyncEnumerable）：原文里不会有分隔行与 Content-Disposition，"
                + "即使 ContentType 自带 boundary，服务端也无法解析。请传入对象或字典由库组装 multipart 字段，"
                + "或用 HttpContent（如 MultipartFormDataContent）承载预序列化的 multipart 正文。", config);
        }

        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 内容原样透传；仅当内容没有 Content-Type 且用户显式配置过时才补头部。
                if (config.ContentTypeSet && existing.Headers.ContentType is null)
                {
                    RequestContentType.Set(existing, mediaType, config);
                }

                // multipart 没有 boundary 必然解析失败。
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

            // 流式正文：按块写出、不缓冲，长度未知。
            case IAsyncEnumerable<byte[]> chunks:
                return CreateStreaming(AsyncEnumerableBodyContent.FromByteChunks(chunks), mediaType, config);

            case IAsyncEnumerable<ReadOnlyMemory<byte>> chunks:
                return CreateStreaming(AsyncEnumerableBodyContent.FromMemoryChunks(chunks), mediaType, config);

            // 声明式 API 的默认正文：未标注成员按键名聚合，按 Content-Type 选 JSON / 表单。
            case AggregateBody aggregate:
                return CreateAggregate(aggregate, config, mediaType, encoding, appendCharset);
        }

        // 其余 IAsyncEnumerable<T> 不能当正文，直接拦下。
        if (TypeFacts.IsAsyncEnumerable(data.GetType()))
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

    /// <summary>文本正文：按生效编码取字节，按需压缩并写 Content-Type。</summary>
    private static HttpContent CreateText(string text, string mediaType, Encoding encoding, bool appendCharset, AxiosConfig config)
    {
        var content = Compression.WrapBytes(encoding.GetBytes(text), config);
        RequestContentType.Set(content, RequestContentType.Compose(mediaType, encoding, appendCharset), config);
        return content;
    }

    /// <summary>字节正文：按配置压缩后挂上 <c>Content-Type</c>。</summary>
    private static HttpContent CreateBinary(byte[] bytes, string mediaType, AxiosConfig config)
    {
        var content = Compression.WrapBytes(bytes, config);
        RequestContentType.Set(content, mediaType, config);
        return content;
    }

    /// <summary>零拷贝发送 <see cref="ReadOnlyMemory{T}"/>；开启请求体压缩时先压成字节。</summary>
    private static HttpContent CreateBinary(ReadOnlyMemory<byte> memory, string mediaType, AxiosConfig config)
    {
        HttpContent content;
        if (Compression.IsEnabled(config))
        {
            content = Compression.WrapBytes(memory.ToArray(), config);
        }
        else
        {
            content = new ReadOnlyMemoryContent(memory);
        }

        RequestContentType.Set(content, mediaType, config);
        return content;
    }

    /// <summary>默认正文的分派：form-urlencoded 展平；JSON 按成员声明类型写出；multipart 与其它序列化器报错。</summary>
    private static HttpContent CreateAggregate(
        AggregateBody body,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        if (MediaTypes.Is(mediaType, AxiosConfig.FormUrlEncodedContentType))
        {
            return FormBodyEncoder.CreateAggregate(body, config, mediaType, encoding, appendCharset);
        }

        if (MediaTypes.Is(mediaType, MultipartMediaType))
        {
            throw AxiosException.InvalidOption(
                "按键聚合的正文（[Body(\"名\")] / 未标注成员按键名聚合）不能按 multipart 发送："
                + "请给请求模型标注 [Multipart] 由库组装表单，或改用无参 [Body]。", config);
        }

        var serializer = AxiosContent.Get(config, mediaType);
        if (!ReferenceEquals(serializer, SystemTextJsonSerializer.Instance))
        {
            throw AxiosException.InvalidOption(
                "按键聚合的正文（[Body(\"名\")] / 未标注成员按键名聚合）只支持 JSON 与 application/x-www-form-urlencoded："
                + $"当前生效的序列化器是 {serializer.GetType().Name}。"
                + "[Body(\"名\")] 只给正文键起名、不改变通道支持；"
                + "要把整个对象原样交给该序列化器，请用无参 [Body]（整份正文，不与其它正文来源共存）。", config);
        }

        if (!config.ContentTypeSet && serializer.MediaType is { } declared && !string.IsNullOrWhiteSpace(declared))
        {
            mediaType = declared.Trim();
        }

        try
        {
            // UTF-8 直接出字节。
            if (SystemTextJsonSerializer.EmitsUtf8(config))
            {
                return CreateBinary(SystemTextJsonSerializer.SerializeAggregate(body, config), mediaType, config);
            }

            var text = CreateText(
                Encoding.UTF8.GetString(SystemTextJsonSerializer.SerializeAggregate(body, config)),
                mediaType,
                encoding,
                appendCharset,
                config);
            ValidateDeclaredCharset(text, config, encoding);
            return text;
        }
        catch
        {
            // 正文不再产出：成员里的流 / 文件内容按 FormFile 契约回收。
            body.DisposeOwned();
            throw;
        }
    }

    /// <summary>结构化数据的分派：form-urlencoded / multipart 走专用编码器，其余交给序列化器（标量直接取文本）。</summary>
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

        // 未显式设置 ContentType 时采用序列化器自报的媒体类型。
        var serializer = AxiosContent.Get(config, mediaType);
        if (!config.ContentTypeSet && serializer.MediaType is { } declared && !string.IsNullOrWhiteSpace(declared))
        {
            mediaType = declared.Trim();
        }

        // 已定型的裸标量：文本由调用方产出。
        if (data is PreformattedValue formatted)
        {
            var formattedContent = CreateText(formatted.Text, mediaType, encoding, appendCharset, config);
            ValidateDeclaredCharset(formattedContent, config, encoding);
            return formattedContent;
        }

        // 基础类型不经过序列化器，直接取文本。
        if (ScalarTypes.IsScalarType(type))
        {
            // 标量正文：钩子 > 通道默认。
            var scalar = CreateText(
                ValueText.Format(data, ValueTextChannel.ScalarBody, memberFormat: null, config.SerializationOrNull?.ValueText)!,
                mediaType,
                encoding,
                appendCharset,
                config);
            ValidateDeclaredCharset(scalar, config, encoding);
            return scalar;
        }

        // 声明成 XML / YAML 却用内置 JSON 序列化器写正文：这两族是库里有专用序列化器的结构化格式，
        // 写出 JSON 字节挂着 XML 头服务端必然解析失败，属于选项错误（其它媒体类型仍交给当前序列化器）。
        // 位置在两条出口之前：JSON 序列化器会走 SerializeToBytes 的零拷贝出口，放到字节分支后面就永远轮不到。
        if (config.ContentTypeSet && MediaTypes.IsXmlOrYaml(mediaType) && ReferenceEquals(serializer, SystemTextJsonSerializer.Instance))
        {
            throw AxiosException.InvalidOption(
                $"ContentType 是 {mediaType}，但没有为它注册 IContentSerializer：内置 JSON 序列化器只会写 JSON 字节，"
                + "服务端按声明的媒体类型解析必然失败。请用 AxiosContent.Register / "
                + "Serialization.BodySerializers.Register 注册该媒体类型的序列化器（内置的有 XmlContentSerializer），"
                + "或把 ContentType 改回 JSON，或由你自己序列化后用 string / byte[] 传入。", config);
        }

        // 二进制格式的请求出口：非 null 表示该格式以字节为正文，原样发送。
        if (serializer.SerializeToBytes(data, type, config) is { } bytes)
        {
            return CreateBinary(bytes, mediaType, config);
        }

        // Serialize 可能写 Content-Type，产出之后再校验。
        var text = CreateText(serializer.Serialize(data, type, config), mediaType, encoding, appendCharset, config);
        ValidateDeclaredCharset(text, config, encoding);
        return text;
    }

    /// <summary>正文产出后校验最终 Content-Type 的 charset 与正文编码一致（请求头优先）。</summary>
    private static void ValidateDeclaredCharset(HttpContent content, AxiosConfig config, Encoding bodyEncoding)
    {
        var finalType = config.HasHeaders && config.Headers.Get("Content-Type") is { Length: > 0 } headerType
            ? headerType
            : content.Headers.ContentType?.ToString();

        if (finalType is { Length: > 0 }
            && RequestContentType.ResolveCharset(finalType, config) is { } declared
            && declared.CodePage != bodyEncoding.CodePage)
        {
            throw AxiosException.InvalidOption(
                $"Content-Type 声明 charset={declared.WebName}，但请求体实际按 {bodyEncoding.WebName} 编码："
                + "两者不一致会让服务端按头解出乱码。请让序列化器自报 / 写入与正文一致的 charset，"
                + "或改用 AxiosConfig.ContentType（它的 charset 会驱动正文编码）与 Serialization.TextEncoding。", config);
        }
    }

    /// <summary>开启请求体压缩时，拒绝没有完整字节可压的正文形态。</summary>
    private static void EnsureCompressible(object data, string mediaType, AxiosConfig config)
    {
        const string Hint = "请先压缩好再传入，或改用对象 / byte[] 让库压缩，或去掉 Compression。";

        if (data is Stream)
        {
            throw AxiosException.InvalidOption(
                "Compression 不压缩 Stream 正文：流式正文没有完整长度，压缩需要先缓冲整个内容。" + Hint, config);
        }

        if (data is IAsyncEnumerable<byte[]> or IAsyncEnumerable<ReadOnlyMemory<byte>>)
        {
            throw AxiosException.InvalidOption(
                "Compression 不压缩流式正文（IAsyncEnumerable 字节块）：内容按块写出、长度未知。" + Hint, config);
        }

        if (data is HttpContent)
        {
            throw AxiosException.InvalidOption(
                "Compression 不作用于调用方传入的 HttpContent：压缩与 Content-Encoding 由该内容自己决定。" + Hint, config);
        }

        if (MediaTypes.Is(mediaType, MultipartMediaType))
        {
            throw AxiosException.InvalidOption(
                "Compression 不压缩 multipart 正文：部件里可能含流式文件。" + Hint, config);
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

    /// <summary>正文是否由本库按 <see cref="Encoding"/> 编成文本（string / 已定型的标量 / 结构化数据）。</summary>
    private static bool EncodesText(object data)
        => data is string
            // 兜底条件已覆盖它，这里显式列出。
            || data is PreformattedValue
            || (data is not HttpContent
                && data is not byte[]
                && data is not ReadOnlyMemory<byte>
                && data is not Memory<byte>
                && data is not ArraySegment<byte>
                && data is not Stream
                && data is not IAsyncEnumerable<byte[]>
                && data is not IAsyncEnumerable<ReadOnlyMemory<byte>>);
}
