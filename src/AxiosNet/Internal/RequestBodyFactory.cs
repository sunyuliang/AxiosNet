using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 把 <see cref="AxiosConfig.Data"/> 转成 <see cref="HttpContent"/>。
/// <para>
/// 唯一约定：<b>请求体格式由 <see cref="AxiosConfig.ContentType"/> 决定，与 data 的运行时类型无关</b>。
/// 例外是「调用方没表态」：未显式设置时采用生效序列化器自报的 <see cref="IContentSerializer.MediaType"/>。
/// </para>
/// <list type="bullet">
/// <item>string / byte[] / Memory / ArraySegment / Stream / HttpContent：原样发送，只应用 Content-Type。</item>
/// <item>基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）：直接取文本，<b>不经过序列化器</b>。</item>
/// <item>结构化数据：json → 序列化器；form-urlencoded → <see cref="FormBodyEncoder"/>；multipart → <see cref="MultipartBodyBuilder"/>。</item>
/// </list>
/// </summary>
internal static class RequestBodyFactory
{
    private const string MultipartMediaType = AxiosContentType.MultipartFormData;

    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 请求体文本编码：默认 UTF-8；charset 只在显式设置 RequestEncoding 时写入，
        // 避免给 json 强加 charset 参数。
        var encoding = config.RequestEncoding ?? Encoding.UTF8;
        var appendCharset = config.RequestEncoding is not null;
        var mediaType = config.ContentType;

        // multipart + 原始内容是矛盾的组合：没有 boundary 服务端无从解析，有条 boundary 也说明不了字段名。
        // 要发预序列化的 multipart 正文，请用 HttpContent 作为 data。
        if (data is not null and not HttpContent
            && IsRawContent(data)
            && MediaTypes.Is(mediaType, MultipartMediaType)
            && RequestContentType.ExtractBoundary(mediaType) is null)
        {
            throw AxiosException.InvalidOption(
                "Content-Type 为 multipart/form-data 时，data 不能是原始内容"
                + "（string / byte[] / Memory / Stream）：请传入对象或字典由库组装 multipart 字段，"
                + "或用 HttpContent（如 MultipartFormDataContent）承载预序列化的 multipart 正文。",
                config);
        }

        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 已构造好的内容原样透传；仅当内容自身没有 Content-Type 且用户显式配置过时才补头部。
                if (config.ContentTypeSet && existing.Headers.ContentType is null)
                {
                    RequestContentType.Set(existing, mediaType);
                }

                // multipart 没有 boundary 服务端必然解析失败（正文靠随机边界切分部件），
                // 与其发出去换一个看不懂的 400，不如在这里拦。
                if (existing.Headers.ContentType?.MediaType is { } existingMediaType
                    && existingMediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                    && !RequestContentType.HasMultipartBoundary(existing.Headers.ContentType))
                {
                    throw AxiosException.InvalidOption(
                        "Content-Type 为 multipart 却没有 boundary：data 传入的 HttpContent 必须自带合法边界"
                        + "（如 MultipartFormDataContent），或把对象/字典交给库组装字段。",
                        config);
                }

                return existing;

            case string text:
                return CreateText(text, mediaType, encoding, appendCharset);

            case byte[] bytes:
                return CreateBinary(bytes, mediaType);

            case ReadOnlyMemory<byte> memory:
                return CreateBinary(memory, mediaType);

            case Memory<byte> memory:
                return CreateBinary(memory, mediaType);

            case ArraySegment<byte> segment:
                return CreateBinary(segment.AsMemory(), mediaType);

            case Stream stream:
                var streamContent = new StreamContent(stream);
                RequestContentType.Set(streamContent, mediaType);
                return streamContent;
        }

        return CreateStructured(data, config, mediaType, encoding, appendCharset);
    }

    private static HttpContent CreateText(string text, string mediaType, Encoding encoding, bool appendCharset)
    {
        // 直接构造 ByteArrayContent 可保证携带 Content-Length，避免分块传输带来的兼容性问题。
        var content = new ByteArrayContent(encoding.GetBytes(text));
        RequestContentType.Set(content, RequestContentType.Compose(mediaType, encoding, appendCharset));
        return content;
    }

    private static HttpContent CreateBinary(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        RequestContentType.Set(content, mediaType);
        return content;
    }

    /// <summary>
    /// 零拷贝发送 <see cref="ReadOnlyMemory{T}"/>：<see cref="ByteArrayContent"/> 会先 ToArray() 复制整段。
    /// </summary>
    private static HttpContent CreateBinary(ReadOnlyMemory<byte> memory, string mediaType)
    {
        var content = new ReadOnlyMemoryContent(memory);
        RequestContentType.Set(content, mediaType);
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

        // 基础类型<b>不经过序列化器</b>：请求体语义就是「这个值本身」，直接取文本。
        // 交给序列化器既多余（JSON 裸标量与本函数结果一致）又不可行（XML / YAML 无法表达无名根标量）。
        // 字典 / 集合 / 对象 / dynamic 一律不进这个分支，它们和业务对象走同一套逻辑。
        if (BodyFields.IsScalar(type))
        {
            return CreateText(BodyFields.ScalarBodyText(data), mediaType, encoding, appendCharset);
        }

        // 其余结构化数据交给生效序列化器：显式注入优先，未注入时按 mediaType 自动匹配（再回落内置 JSON）。
        // 二进制载荷请由调用方序列化成 byte[] / Stream 传入。
        var serializer = AxiosContent.Get(config, mediaType);

        // 头由正文的产出者自报，与 axios 的 headers.setContentType('application/json', false) 同构：
        // 调用方没显式设置时采用序列化器声明的媒体类型，显式设置过就绝不覆盖。
        if (!config.ContentTypeSet && serializer.MediaType is { } declared && !string.IsNullOrWhiteSpace(declared))
        {
            mediaType = declared.Trim();
        }

        return CreateText(serializer.Serialize(data, type, config), mediaType, encoding, appendCharset);
    }

    /// <summary>「内容原样发送」的类型：与结构化数据相对，不经过任何展平/序列化。</summary>
    private static bool IsRawContent(object data)
        => data is string or byte[] or ReadOnlyMemory<byte> or Memory<byte> or ArraySegment<byte> or Stream;
}
