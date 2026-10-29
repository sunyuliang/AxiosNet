using System.Buffers;
using System.Collections;
using System.Net.Http.Headers;
using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 响应阶段：把 <see cref="System.Net.Http.HttpResponseMessage"/> 转成 <see cref="AxiosResponse"/>，
/// 含响应头上下文、正文读取、responseType 与目标类型分派、序列化器选型。只读配置，不持有实例状态。
/// </summary>
internal static class ResponseReader
{
    /// <summary>
    /// 下载侧包装：按 Content-Length 预检 maxContentLength，再按需套进度 / 限速 / 长度上限流；
    /// 都不需要时原样返回。长度上限在流上生效，chunked 响应同样受约束。
    /// </summary>
    private static Stream WrapDownloadStream(
        Stream stream,
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config)
    {
        var declared = message.Content.Headers.ContentLength;
        var limits = config.LimitsOrNull;
        var callbacks = config.CallbacksOrNull;

        if (limits?.MaxContentLength is long limit && declared is long length && length > limit)
        {
            stream.Dispose();
            throw AxiosException.MaxContentLength(config, request, length, limit);
        }

        if (callbacks?.OnDownloadProgress is null && limits?.MaxRate is not > 0 && limits?.MaxContentLength is null)
        {
            return stream;
        }

        return new ProgressReadStream(
            stream,
            declared,
            callbacks?.OnDownloadProgress,
            limits?.MaxRate,
            limits?.MaxContentLength,
            config,
            request);
    }

    /// <summary>段式累积的段大小：起步 80 KB，读满一段就倍增，到 8 MB 后等长追加。</summary>
    private const int InitialSegment = 81920;

    private const int MaxSegment = 8 * 1024 * 1024;

    /// <summary>声明长度的预分配信任上限：不超过它就按声明值一次分配到位，超过就走段式累积。</summary>
    private const long MaxPreallocated = 8 * 1024 * 1024;

    /// <summary>
    /// 读取整个响应体：声明长度在信任上限内时按声明值预分配，否则段式累积；两条路径都按真实字节数执行 maxContentLength。
    /// 本方法只管读，不释放流。
    /// </summary>
    private static Task<byte[]> ReadAllBytesAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        long? declaredLength)
        => declaredLength is > 0 and <= MaxPreallocated
            ? ReadDeclaredLengthAsync(stream, config, request, cancellationToken, (int)declaredLength.Value)
            : ReadSegmentedAsync(stream, config, request, cancellationToken, prefix: null);

    /// <summary>
    /// 已知长度（≤ 信任上限）的读取：按声明值预分配；读满后再探一次确认没有尾部数据，
    /// 多出数据时转段式累积，恰好命中时零拷贝交出。
    /// </summary>
    private static async Task<byte[]> ReadDeclaredLengthAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        int capacity)
    {
        var buffer = new byte[capacity];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // 连接提前关闭：裁剪掉尾部未写区域，不交给调用方。
                return Trim(buffer, filled);
            }

            filled += read;
        }

        // 声明值已读满：再读一块确认正文真的结束（此时不能直接交出 buffer）。
        var probe = ArrayPool<byte>.Shared.Rent(InitialSegment);
        int more;
        try
        {
            more = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AxiosException)
        {
            // 探测失败不影响已按声明读满的正文；AxiosException 除外（可能是正文上限被触发）。
            ArrayPool<byte>.Shared.Return(probe);
            return buffer;
        }

        if (more == 0)
        {
            // 声明值与正文一致：零拷贝交出。
            ArrayPool<byte>.Shared.Return(probe);
            return buffer;
        }

        return await ReadSegmentedAsync(stream, config, request, cancellationToken, prefix: buffer, current: probe, filled: more)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 段式累积：从 <see cref="ArrayPool{T}"/> 租段、倍增增长，收齐后装配一个精确数组。
    /// <paramref name="prefix"/> 是所有权已交出的预分配数组（不归还池）；
    /// <paramref name="current"/> / <paramref name="filled"/> 允许调用方交进一段已读过若干字节的池化段。
    /// </summary>
    private static async Task<byte[]> ReadSegmentedAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        byte[]? prefix,
        byte[]? current = null,
        int filled = 0)
    {
        var segments = new List<byte[]>(4);

        // segments 的前 ownedCount 段归调用方（预分配前缀），不归还池。
        var ownedCount = 0;
        var total = 0L;

        if (prefix is not null)
        {
            segments.Add(prefix);
            ownedCount = 1;
            total = prefix.Length;
        }

        current ??= ArrayPool<byte>.Shared.Rent(InitialSegment);
        total += filled;

        try
        {
            while (true)
            {
                // 上限按真实字节数判（含预分配前缀与探测块），声明长度不参与。
                if (config.LimitsOrNull?.MaxContentLength is long limit && total > limit)
                {
                    throw AxiosException.MaxContentLength(config, request, total, limit);
                }

                if (filled == current.Length)
                {
                    // 读满一段：先租到下一段再交出当前段，避免重复归还。
                    var next = ArrayPool<byte>.Shared.Rent(Math.Min(current.Length * 2, MaxSegment));
                    segments.Add(current);
                    current = next;
                    filled = 0;
                }

                var read = await stream.ReadAsync(current.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
                total += read;
            }

            // 装配成精确长度的成品；超过 Array.MaxLength 时换成统一错误模型。
            if (total > Array.MaxLength)
            {
                throw AxiosException.ResponseTooLarge(config, request, total);
            }

            return Assemble(segments, current, filled, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(current);

            for (var i = ownedCount; i < segments.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(segments[i]);
            }
        }
    }

    /// <summary>把「若干已读满的段 + 尾段的有效字节」装配成一个精确长度数组。</summary>
    private static byte[] Assemble(List<byte[]> segments, byte[] current, int filled, long total)
    {
        var result = new byte[total];
        var offset = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            Array.Copy(segment, 0, result, offset, segment.Length);
            offset += segment.Length;
        }

        if (filled > 0)
        {
            Array.Copy(current, 0, result, offset, filled);
        }

        return result;
    }

    /// <summary>把预分配缓冲裁剪成实际长度（读到一半连接关闭时用）。</summary>
    private static byte[] Trim(byte[] buffer, int filled)
    {
        var trimmed = new byte[filled];
        buffer.AsSpan(0, filled).CopyTo(trimmed);
        return trimmed;
    }

    /// <summary>
    /// 读响应并转成 <see cref="AxiosResponse"/>：状态码与响应头 → 正文（流式交出包装流，其余读全量字节）
    /// → 按 responseType 与目标类型分派为 Data。<paramref name="lenient"/> 为 true 时解析失败回退原始内容。
    /// </summary>
    public static async Task<AxiosResponse> ReadAsync(
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config,
        CancellationToken cancellationToken,
        Type? dataType,
        AxiosResponse? target,
        bool lenient)
    {
        // 保留两套头集合的惰性只读视图，首次访问 Headers 时才物化成字典。
        var headers = new ResponseHeadersView(message.Headers, message.Content.Headers);

        var status = (int)message.StatusCode;
        var statusText = StatusTextMap.Get(status);

        // 反序列化 / transformResponse 阶段可读的响应上下文，复用刚建好的 headers。
        var contentType = message.Content.Headers.ContentType;
        config.Response = new AxiosResponseInfo(status, statusText, headers, contentType?.MediaType, contentType?.CharSet);

        object? data;
        // 已解码的正文，供错误体读取与诊断（二进制 / 流为 null）。
        string? rawText = null;
        // 实际收到的正文字节数；流式路径无法统计，为 null。
        long? bodyBytes = null;
        if (config.ResponseType == ResponseType.Stream)
        {
            var responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            data = WrapDownloadStream(responseStream, message, request, config);
        }
        else
        {
            Stream responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var downloadStream = WrapDownloadStream(responseStream, message, request, config);
            var declared = message.Content.Headers.ContentLength;
            byte[] bytes;
            try
            {
                bytes = await ReadAllBytesAsync(downloadStream, config, request, cancellationToken, declared).ConfigureAwait(false);
            }
            finally
            {
                // 释放由这一层负责，读失败同样要还连接。
                downloadStream.Dispose();
            }

            bodyBytes = bytes.Length;

            // 原始字节钩子：在字符集解码 / 反序列化 / transformResponse 之前触发，正文仍是 HttpClient 交付的原样字节。
            // 不复制：回调期间零拷贝借用；需要长期持有由调用方自行复制。
            if (config.CallbacksOrNull?.OnRawResponse is { } onRawResponse)
            {
                try
                {
                    onRawResponse(config.Response!, bytes.AsMemory());
                }
                catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
                {
                    throw AxiosException.CallbackFailed(config, request, nameof(AxiosConfig.CallbackSettings.OnRawResponse), ex, upload: false);
                }
            }

            data = ConvertBody(
                config,
                request,
                dataType,
                bytes,
                contentType?.MediaType,
                contentType?.CharSet,
                lenient,
                out rawText);
        }

        // 泛型调用方传入的目标响应对象就地填充，避免每请求多造一个响应对象。
        var response = target ?? new AxiosResponse();
        response.Data = data;

        // 只在状态码不合法时保留原文，避免大正文跟着响应对象一直存活。
        response.RawText = lenient ? rawText : null;
        response.Status = status;
        response.StatusText = statusText;
        // 实际协商出的协议版本（降级是静默的）。
        response.ProtocolVersion = message.Version;
        response.BodyBytes = bodyBytes;
        response.Headers = headers;
        response.Config = config;
        response.Request = request;
        return response;
    }

    /// <summary>是否需要把响应字节解码成文本：arraybuffer / stream / byte[] 只要字节，其余都要文本。</summary>
    private static bool NeedsText(AxiosConfig config, Type? dataType, bool textual) => config.ResponseType switch
    {
        ResponseType.ArrayBuffer => false,
        ResponseType.Stream => false,
        ResponseType.Text or ResponseType.Dynamic => true,
        _ => textual && dataType != typeof(byte[])
    };

    /// <summary>body 是否只含空白字符（<c>""</c> 与 <c>"  \r\n"</c> 语义等价）。</summary>
    private static bool IsBlank(string text) => text.AsSpan().Trim().IsEmpty;

    /// <summary>文本是否可能是一段 JSON 文档：只看首个非空白字符，<c>\uFEFF</c> 按空白跳过。</summary>
    private static bool LooksLikeJson(string text)
    {
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\uFEFF')
            {
                continue;
            }

            return c is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || (c >= '0' && c <= '9');
        }

        return false;
    }

    /// <summary>未解码时的空白判定：只认 ASCII 空白（SP/HTAB/CR/LF）。</summary>
    private static bool IsBlankUtf8(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 从原始字节装配 Data：解码 → 按 responseType / 目标类型转换 → transformResponse，网络与短路响应共用。
    /// <paramref name="rawText"/> 回传解码后的正文（只要字节的分支为 <c>null</c>）。
    /// </summary>
    internal static object? ConvertBody(
        AxiosConfig config,
        HttpRequestMessage? request,
        Type? dataType,
        byte[] bytes,
        string? mediaType,
        string? charSet,
        bool lenient,
        out string? rawText)
    {
        // 文本还是二进制由 Content-Type 决定（缺失视为文本）。
        var textual = MediaTypes.IsTextual(mediaType);

        // 只要字节的调用不解码，其余只解码一次，文本在 ConvertData 里按需使用。
        var text = NeedsText(config, dataType, textual)
            ? ResponseTextDecoder.Decode(bytes, charSet, config.SerializationOrNull?.ResponseEncoding)
            : null;

        rawText = text;

        object? data = ConvertData(config, request, dataType, bytes, text, charSet, textual, mediaType, lenient);

        // transformResponse：反序列化之后依次加工 Data。
        // 调用方代码抛什么都在这里归一到 ERR_BAD_RESPONSE，不让裸异常从管线深处漏出。
        if (config.TransformResponseSnapshot is { } responseTransforms)
        {
            foreach (var transform in responseTransforms)
            {
                try
                {
                    data = transform(data, config);
                }
                catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
                {
                    throw AxiosException.ResponseTransformFailed(config, request, ex);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// 短路响应的装配：语义与 <see cref="ReadAsync"/> 一致，只是正文不来自网络。
    /// <see cref="AxiosShortCircuit.Body"/> 按本次调用重新解析，<see cref="AxiosShortCircuit.Data"/> 原样交付（不跑 transformResponse）。
    /// <paramref name="lenient"/> 为 true 时解析失败回退原文，与网络路径同口径。
    /// </summary>
    internal static AxiosResponse ReadShortCircuit(
        AxiosShortCircuit reply,
        AxiosConfig config,
        Type? dataType,
        AxiosResponse? target,
        bool lenient)
    {
        var status = reply.Status;

        if (status is < 100 or > 999)
        {
            throw AxiosException.InvalidOption(
                $"AxiosShortCircuit.Status 必须在 100–999 之间（当前 {status}）：它是最终响应的状态码，"
                + "不合法会让 validateStatus 与错误模型都无从判断。", config);
        }

        // Body 与 Data 是同一件事的两种形态，同时给出视为配置错误。
        if (reply.Body is not null && reply.Data is not null)
        {
            throw AxiosException.InvalidOption(
                "AxiosShortCircuit 不能同时设置 Body 与 Data：Body 是原始正文（按本次调用的类型重新解析），"
                + "Data 是已成型的结果（原样交付），请二选一。", config);
        }

        var statusText = reply.StatusText ?? StatusTextMap.Get(status);
        var headers = BuildShortCircuitHeaders(reply, config);

        // 与网络路径同一份上下文：Headers 与 response.Headers 同实例。
        var (mediaType, charSet) = ReadContentType(headers);
        config.Response = new AxiosResponseInfo(status, statusText, headers, mediaType, charSet);

        object? data;
        string? rawText = null;
        long? bodyBytes = null;

        if (reply.Body is { } body)
        {
            // 体积上限同样生效：与网络路径同口径，超限直接失败。
            if (config.LimitsOrNull?.MaxContentLength is long maxLength && body.Length > maxLength)
            {
                throw AxiosException.MaxContentLength(config, null, body.Length, maxLength);
            }

            bodyBytes = body.Length;

            if (config.ResponseType == ResponseType.Stream)
            {
                // 流式：正文以流交出，底层字节数组必须复制。
                data = new MemoryStream((byte[])body.Clone(), writable: false);
            }
            else
            {
                data = ConvertBody(config, null, dataType, body, mediaType, charSet, lenient, out rawText);

                // 字节形态原样交出，这里必须复制，保证每份响应都是独立对象。
                if (ReferenceEquals(data, body))
                {
                    data = (byte[])body.Clone();
                }
            }
        }
        else
        {
            data = reply.Data;

            // 已成型结果里只有字符串能充当错误原文。
            rawText = lenient ? data as string : null;
        }

        var response = target ?? new AxiosResponse();
        response.Data = data;
        response.RawText = lenient ? rawText : null;
        response.Status = status;
        response.StatusText = statusText;
        response.ProtocolVersion = reply.ProtocolVersion;
        response.BodyBytes = bodyBytes;
        response.Headers = headers;
        response.Config = config;

        // 没有发出请求，Request 保持 null。
        response.Request = null;
        return response;
    }

    /// <summary>
    /// 短路响应头：调用方给的字典搬进大小写不敏感的只读字典，<see cref="AxiosShortCircuit.ContentType"/>
    /// 作为 <c>Content-Type</c> 覆盖同名项。头名与头值走与请求头相同的校验，非法时归一到 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </summary>
    private static Dictionary<string, IEnumerable<string>> BuildShortCircuitHeaders(
        AxiosShortCircuit reply,
        AxiosConfig config)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (reply.Headers is { Count: > 0 } source)
            {
                foreach (var header in source)
                {
                    // 空头名 / null 值视为没有这个头，静默跳过。
                    if (string.IsNullOrEmpty(header.Key) || header.Value is null)
                    {
                        continue;
                    }

                    // 值挡 C0 注入，名挡非 token，与请求头同一把尺子。
                    var name = AxiosHeaders.ValidateHeaderName(header.Key);
                    AxiosHeaders.ValidateHeaderValue(name, header.Value);
                    headers[name] = new[] { header.Value };
                }
            }

            if (reply.ContentType is { Length: > 0 } contentType)
            {
                AxiosHeaders.ValidateHeaderValue("Content-Type", contentType);
                headers["Content-Type"] = new[] { contentType };
            }
        }
        catch (ArgumentException ex)
        {
            throw AxiosException.InvalidOption($"短路响应的响应头非法：{ex.Message}", config);
        }

        return headers;
    }

    /// <summary>从响应头取出媒体类型与字符集（短路响应的头是字典，只能自己解析）。</summary>
    private static (string? MediaType, string? CharSet) ReadContentType(
        IReadOnlyDictionary<string, IEnumerable<string>> headers)
    {
        if (headers.TryGetValue("Content-Type", out var values)
            && values.FirstOrDefault() is { Length: > 0 } value
            && MediaTypeHeaderValue.TryParse(value, out var parsed))
        {
            return (parsed.MediaType, parsed.CharSet);
        }

        return (null, null);
    }

    /// <summary>
    /// 按 responseType / 目标类型把原始字节与已解码文本转换为 Data：字节、文本、dynamic 图、基础类型
    /// 由框架给出，只有业务对象交给 <see cref="IContentSerializer"/>。<paramref name="lenient"/> 为 true 时解析失败返回原始内容。
    /// </summary>
    private static object? ConvertData(
        AxiosConfig config,
        HttpRequestMessage? request,
        Type? dataType,
        byte[] bytes,
        string? text,
        string? charSet,
        bool textual,
        string? mediaType,
        bool lenient)
    {
        // 按需解码：只要字节的分支不做 bytes → UTF-16 转换。
        string Text() => text ??= ResponseTextDecoder.Decode(bytes, charSet, config.SerializationOrNull?.ResponseEncoding);

        // 1) 显式 responseType 优先：arraybuffer 直接给原始字节。
        if (config.ResponseType == ResponseType.ArrayBuffer)
        {
            return bytes;
        }

        // 2) text 直接给原始文本（不做任何解析）。
        if (config.ResponseType == ResponseType.Text)
        {
            return Text();
        }

        // 3) dynamic 展开：JSON 特有的「无类型对象图」，由框架解析，不经过序列化器。
        if (config.ResponseType == ResponseType.Dynamic)
        {
            var json = Text();
            if (IsBlank(json))
            {
                return null;
            }

            try
            {
                return DynamicJson.Parse(json);
            }
            catch (Exception ex) when (ex is not AxiosException)
            {
                // 非 JSON 响应走统一错误模型。
                if (lenient)
                {
                    return json;
                }

                throw AxiosException.BadResponse(config, request, "dynamic JSON", ex);
            }
        }

        // 4) 目标类型就是原始内容本身：string 原样返回（支持纯文本），byte[] 直接给字节。
        if (dataType == typeof(string))
        {
            return Text();
        }

        if (dataType == typeof(byte[]))
        {
            return bytes;
        }

        // 5) 空 body 与只含空白的 body 都视为 null。
        if (text is null ? IsBlankUtf8(bytes) : IsBlank(text))
        {
            return null;
        }

        // 6) 没有目标类型（非泛型 / JsonElement）：框架按 JSON 文档形态给出，解析失败原样返回文本。
        if (dataType is null || dataType == typeof(object) || dataType == typeof(JsonElement))
        {
            // 二进制响应没有文档形态，原样给字节。
            if (!textual)
            {
                return bytes;
            }

            var document = Text();

            // 首字符不是 JSON 值的起始记号时直接返回文本，省掉一次必然失败的 Parse。
            if (!LooksLikeJson(document))
            {
                return document;
            }

            try
            {
                using var parsed = JsonDocument.Parse(document);
                return parsed.RootElement.Clone();
            }
            catch (Exception ex) when (ex is not AxiosException)
            {
                return document;
            }
        }

        // 7) 二进制响应：不做解码，交给序列化器的字节入口（默认实现按 UTF-8 转调文本入口）。
        if (!textual)
        {
            try
            {
                return AxiosContent.Get(config, mediaType).Deserialize(bytes, dataType, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // lenient：解析失败不抛，原样回字节（此时没有文本可回退）。
                if (lenient)
                {
                    return bytes;
                }

                throw AxiosException.BadResponse(config, request, dataType, ex);
            }
        }

        var raw = Text();

        // 8) 基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）：框架直接解析文本，带引号形态交给序列化器兜底。
        if (ScalarParser.TryParse(raw, dataType, out var scalar))
        {
            return scalar;
        }

        // 9) 文本响应的业务对象：交给当前序列化器。
        try
        {
            var deserialized = AxiosContent.Get(config, mediaType).Deserialize(raw, dataType, config);

            // 序列化器不复核枚举已定义值，与第 8 步同一把尺子。
            if (deserialized is Enum enumValue && !ScalarParser.IsAcceptableEnumValue(dataType, enumValue))
            {
                // lenient 时不因数据值失败：回退原文，不让正文问题盖住 HTTP 状态错误。
                if (lenient)
                {
                    return raw;
                }

                // dataType 可能是 Nullable<Color>，消息里报枚举类型本身。
                throw AxiosException.UndefinedEnumValue(config, request, (Nullable.GetUnderlyingType(dataType) ?? dataType).Name, enumValue.ToString());
            }

            return deserialized;
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            if (lenient)
            {
                return raw;
            }

            throw AxiosException.BadResponse(config, request, dataType, ex);
        }
    }
}

/// <summary>
/// 响应头的惰性只读视图：保留 BCL 的两套头集合，首次访问时才展开成大小写不敏感的字典并缓存。
/// 合并顺序是响应头在前、内容头覆盖同名键，头集合在 <c>HttpResponseMessage.Dispose()</c> 之后仍可读。
/// </summary>
internal sealed class ResponseHeadersView : IReadOnlyDictionary<string, IEnumerable<string>>
{
    private readonly HttpHeaders? _responseHeaders;
    private readonly HttpContentHeaders? _contentHeaders;
    private Dictionary<string, IEnumerable<string>>? _map;

    public ResponseHeadersView(HttpHeaders? responseHeaders, HttpContentHeaders? contentHeaders)
    {
        _responseHeaders = responseHeaders;
        _contentHeaders = contentHeaders;
    }

    private Dictionary<string, IEnumerable<string>> Map => Volatile.Read(ref _map) ?? Build();

    private Dictionary<string, IEnumerable<string>> Build()
    {
        var map = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        if (_responseHeaders is not null)
        {
            foreach (var header in _responseHeaders)
            {
                map[header.Key] = header.Value;
            }
        }

        if (_contentHeaders is not null)
        {
            foreach (var header in _contentHeaders)
            {
                map[header.Key] = header.Value;
            }
        }

        // 并发首次访问可能重复构建，但只发布一份。
        return Interlocked.CompareExchange(ref _map, map, null) ?? map;
    }

    public IEnumerable<string> this[string key] => Map[key];

    public IEnumerable<string> Keys => Map.Keys;

    public IEnumerable<IEnumerable<string>> Values => Map.Values;

    public int Count => Map.Count;

    public bool ContainsKey(string key) => Map.ContainsKey(key);

    public bool TryGetValue(string key, out IEnumerable<string> value) => Map.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumerator() => Map.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
