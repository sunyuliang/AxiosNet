using System.Buffers;
using System.Collections;
using System.Net.Http.Headers;
using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 响应阶段：从 <see cref="System.Net.Http.HttpResponseMessage"/> 到 <see cref="AxiosResponse"/> 的
/// 全部静态逻辑 —— 响应头上下文、正文读取（预分配 / 段式累积）、responseType 与目标类型分派、
/// 序列化器选型、transformResponse 之前的所有转换。
/// <para>请求侧（拦截器 / 请求体 / 发送）留在 <see cref="AxiosClient"/>，本类只读配置、不持有实例状态。</para>
/// </summary>
internal static class ResponseReader
{
    /// <summary>
    /// 下载侧包装：先按 Content-Length 预检 maxContentLength，再按需套进度/限速/长度上限流
    /// （都不需要时原样返回）。
    /// <para>长度上限必须在流上生效，否则 chunked / 未声明长度的流式响应完全没有约束。</para>
    /// </summary>
    private static Stream WrapDownloadStream(
        Stream stream,
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config)
    {
        var declared = message.Content.Headers.ContentLength;

        if (config.MaxContentLength is long limit && declared is long length && length > limit)
        {
            stream.Dispose();
            throw AxiosException.MaxContentLength(config, request, length, limit);
        }

        if (config.OnDownloadProgress is null && config.MaxRate is not > 0 && config.MaxContentLength is null)
        {
            return stream;
        }

        return new ProgressReadStream(
            stream,
            declared,
            config.OnDownloadProgress,
            config.MaxRate,
            config.MaxContentLength,
            config,
            request);
    }

    /// <summary>
    /// 读取整个响应体；服务端未声明长度时按已读字节执行 maxContentLength。
    /// <para>
    /// 分派本身不做成 async：每条非流式请求都过这里，多一个异步状态机帧是纯热路径开销；
    /// 流的释放因此移入两个读取方各自的 finally（每条路径恰好一次）。
    /// </para>
    /// </summary>
    private static Task<byte[]> ReadAllBytesAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        long? declaredLength)
    {
        // 声明了长度且不大时按声明值预分配：恰好写满时直接交底层数组（1× 分配、零拷贝）。
        // 上限 8MB：Content-Length 不可信，不替大额声明预付；超出视为未知长度走段式累积。
        const long maxPreallocated = 8 * 1024 * 1024;
        return declaredLength is > 0 and <= maxPreallocated
            ? ReadPreallocatedAsync(stream, cancellationToken, (int)declaredLength.Value)
            : ReadSegmentedAsync(stream, config, request, cancellationToken);
    }

    /// <summary>
    /// 已知长度（≤ 信任上限）的读取：直接分配精确长度的数组并读满，读满即交出（1× 分配、零拷贝）；
    /// 服务端提前结束时裁剪成实际长度。
    /// <para>
    /// 上限安全：<paramref name="capacity"/> 来自声明长度，读取量又被 capacity 封顶；
    /// 声明长度超过 <see cref="AxiosConfig.MaxContentLength"/> 的情况已在
    /// <see cref="WrapDownloadStream"/> 里拦下，所以这里无需重复判定。
    /// </para>
    /// </summary>
    private static async Task<byte[]> ReadPreallocatedAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int capacity)
    {
        try
        {
            var buffer = new byte[capacity];
            var filled = 0;

            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled == buffer.Length)
            {
                return buffer;
            }

            // 声明长度大于实际字节（连接提前关闭）：裁剪掉尾部零填充，不把未写区域交给调用方。
            var trimmed = new byte[filled];
            buffer.AsSpan(0, filled).CopyTo(trimmed);
            return trimmed;
        }
        finally
        {
            // 本方法拥有流的释放（分派器不做成 async，见 ReadAllBytesAsync）
            stream.Dispose();
        }
    }

    /// <summary>
    /// 段式累积（未声明长度 / 声明值超过信任上限）：从 <see cref="ArrayPool{T}"/> 租段、倍增增长，
    /// 收齐后装配一个精确数组 —— 即 pipeline 的池化分段思想，零依赖。
    /// <para>
    /// 取代的两条老路：按 Content-Length 预付（不可信输入，见 <see cref="ReadAllBytesAsync"/>）
    /// 与 MemoryStream 从 256B 倍增（大响应累计 ≈3× 载荷，且扩容必过冲导致多一次 ToArray）。
    /// 任何尺寸都只付 1× 最终数组，段本身跨请求复用；单段上限约束装配期峰值
    /// （全部段 + 成品 ≈ 2× 载荷）。
    /// </para>
    /// </summary>
    private static async Task<byte[]> ReadSegmentedAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const int initialSegment = 81920;
        const int maxSegment = 8 * 1024 * 1024;

        var segments = new List<byte[]>(4);
        var current = ArrayPool<byte>.Shared.Rent(initialSegment);
        var filled = 0;
        long total = 0;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(current.AsMemory(filled), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
                total += read;

                if (config.MaxContentLength is long limit && total > limit)
                {
                    throw AxiosException.MaxContentLength(config, request, total, limit);
                }

                if (filled < current.Length)
                {
                    continue;
                }

                // 读满一段：入列（段必为读满状态，装配时按整段拷贝），再租下一段（倍增，到上限后等长追加）
                segments.Add(current);
                current = ArrayPool<byte>.Shared.Rent(Math.Min(current.Length * 2, maxSegment));
                filled = 0;
            }

            // 装配：满段整体拷贝 + 末段按实际字节拷贝，得到精确长度的成品（唯一一次全量拷贝）。
            // 先挡一次数组长度上限：超过 Array.MaxLength 时 new byte[total] 只会抛裸 OutOfMemoryException
            // （没有任何上下文），这里换成统一错误模型并指向可用的逃生口。
            if (total > Array.MaxLength)
            {
                throw AxiosException.ResponseTooLarge(config, request, total);
            }

            var result = new byte[total];
            var offset = 0;
            foreach (var segment in segments)
            {
                Array.Copy(segment, 0, result, offset, segment.Length);
                offset += segment.Length;
            }

            Array.Copy(current, 0, result, offset, filled);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(current);
            foreach (var segment in segments)
            {
                ArrayPool<byte>.Shared.Return(segment);
            }

            // 本方法拥有流的释放（分派器不做成 async，见 ReadAllBytesAsync）
            stream.Dispose();
        }
    }

    /// <summary>
    /// 读响应并转成 <see cref="AxiosResponse"/>：状态码与响应头（含给序列化器 /
    /// transformResponse 的只读上下文）→ 正文（流式交出包装流，其余读全量字节）→ 按 responseType
    /// 与目标类型分派为 Data。<paramref name="lenient"/> 为 true 时（状态码不合法）解析失败
    /// 回退原始内容，不抛异常。
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
        // 响应头不在这里展开：多数请求根本不读 Headers，而展开要枚举两套头集合、逐个把头值装箱，
        // 还会触发 BCL 的惰性头值解析（每头约 0.2 µs）。改为保留两套头集合的惰性只读视图，
        // 首次访问 Headers 时才物化成字典。HttpResponseMessage.Dispose() 只释放正文，头集合仍可读。
        var headers = new ResponseHeadersView(message.Headers, message.Content.Headers);

        var status = (int)message.StatusCode;
        var statusText = StatusTextMap.Get(status);

        // 反序列化 / transformResponse 阶段可读的响应上下文：零拷贝复用刚建好的 headers。
        // 序列化器可据此选解析规则（X-Schema-Version），transformResponse 可搬元数据（X-Total-Count）。
        // 响应体的文本/二进制判定不受影响 —— 那由 MediaTypes.IsTextual 决定。
        var contentType = message.Content.Headers.ContentType;
        config.Response = new AxiosResponseInfo(
            status,
            statusText,
            headers,
            contentType?.MediaType,
            contentType?.CharSet);

        object? data;
        // 已解码的响应正文：文本类响应都会在这里留下原文，供错误体读取与诊断（二进制/流为 null）。
        string? rawText = null;
        if (config.ResponseType == ResponseType.Stream)
        {
            var responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            data = WrapDownloadStream(responseStream, message, request, config);
        }
        else
        {
            // 原始内容优先：一律先取原始字节，再按需解码为文本，最后按返回类型转换。
            Stream responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var downloadStream = WrapDownloadStream(responseStream, message, request, config);
            var declared = message.Content.Headers.ContentLength;
            var bytes = await ReadAllBytesAsync(downloadStream, config, request, cancellationToken, declared)
                .ConfigureAwait(false);

            var charSet = contentType?.CharSet;
            var responseMediaType = contentType?.MediaType;

            // 文本还是二进制由 Content-Type 决定（无 Content-Type 视为文本）；二进制连解码都不做。
            var textual = MediaTypes.IsTextual(responseMediaType);

            // 只要字节的调用（arraybuffer / byte[] / 二进制业务对象）不解码；
            // 其余只解码一次，文本在 ConvertData 里按需使用。
            var text = NeedsText(config, dataType, textual)
                ? ResponseTextDecoder.Decode(bytes, charSet, config.ResponseEncoding)
                : null;
            rawText = text;
            data = ConvertData(config, request, dataType, bytes, text, charSet, textual, responseMediaType, lenient);

            // transformResponse：反序列化之后依次加工 Data。
            // 调用方代码抛什么都在这里归一到 ERR_BAD_RESPONSE，不让裸异常从管线深处漏出。
            if (config.HasTransformResponse)
            {
                foreach (var transform in config.TransformResponse)
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
        }

        // 泛型调用方已把目标类型的响应对象造好交过来：就地填充，
        // 否则「管线造一个非泛型响应 + AsTyped 再复制一个」是每请求两个响应对象。
        var response = target ?? new AxiosResponse();
        response.Data = data;

        // 只在「状态码不合法」时保留原文：正常响应保留整段文本会让大正文跟着响应对象一直活着
        // （解析完的 Data 才是调用方要的东西）；错误体的原文是 ApiErrorContext 的唯一可靠来源。
        response.RawText = lenient ? rawText : null;
        response.Status = status;
        response.StatusText = statusText;
        response.Headers = headers;
        response.Config = config;
        response.Request = request;
        return response;
    }

    /// <summary>
    /// 是否真的要把响应字节解码成文本：arraybuffer / stream / byte[] 只要字节；
    /// 二进制响应走序列化器的字节入口；其余形态（text、dynamic、基础类型、文本对象）都需要文本。
    /// </summary>
    private static bool NeedsText(AxiosConfig config, Type? dataType, bool textual) => config.ResponseType switch
    {
        ResponseType.ArrayBuffer => false,
        ResponseType.Stream => false,
        ResponseType.Text or ResponseType.Dynamic => true,
        _ => textual && dataType != typeof(byte[])
    };

    /// <summary>body 是否只含空白字符（<c>""</c> 与 <c>"  \r\n"</c> 语义等价）。</summary>
    private static bool IsBlank(string text) => text.AsSpan().Trim().IsEmpty;

    /// <summary>
    /// 未解码时的空白判定：只含 ASCII 空白（SP/HTAB/CR/LF）即可直接判空，
    /// 非 ASCII 空白（如 NBSP）留给解码后的 <see cref="IsBlank"/>。
    /// </summary>
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
    /// 按 responseType / 目标类型把「原始字节 + 已解码文本」转换为 Data：
    /// 字节、文本、dynamic 图、基础类型由框架给出，只有<b>业务对象</b>交给 <see cref="IContentSerializer"/>。
    /// <para><paramref name="lenient"/> 为 true 时（状态码不合法）解析失败返回原始内容，不抛异常。</para>
    /// </summary>
    private static object? ConvertData(
        AxiosConfig config,
        HttpRequestMessage request,
        Type? dataType,
        byte[] bytes,
        string? text,
        string? charSet,
        bool textual,
        string? mediaType,
        bool lenient)
    {
        // 按需解码：只要字节的分支永远不付 bytes → UTF-16 的代价。
        string Text() => text ??= ResponseTextDecoder.Decode(bytes, charSet, config.ResponseEncoding);

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
                // 非 JSON 响应（HTML 错误页、纯文本）同样走统一错误模型。
                // 这里**有意比 axios 严格**：dynamic 的典型用法是 data.user.name，把 HTML 交回去只会在几十行后炸成
                // RuntimeBinderException；状态码不合法时（lenient）仍然回退原文。
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

        // 5) 空 body 视为 null；只含空白的 body 同样视为空 —— 否则服务端多吐一个换行就会让
        //    typed 请求报 ERR_BAD_RESPONSE，而空 body 却返回 null。
        if (text is null ? IsBlankUtf8(bytes) : IsBlank(text))
        {
            return null;
        }

        // 6) 没有目标类型（非泛型 / JsonElement）：框架按 JSON 文档形态给出，解析失败原样返回文本。
        //    不用序列化器：它没有「文档形态」的概念，而「无类型」也不需要业务反序列化。
        if (dataType is null || dataType == typeof(object) || dataType == typeof(JsonElement))
        {
            // 二进制响应没有文档形态，原样给字节。
            if (!textual)
            {
                return bytes;
            }

            var document = Text();
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

        // 7) 二进制响应（Content-Type 非文本）：不做解码，交给序列化器的字节入口。
        //    文本格式的序列化器不用实现该入口（接口默认按 UTF-8 解码转调文本入口）。
        if (!textual)
        {
            try
            {
                return AxiosContent.Get(config, mediaType).Deserialize(bytes, dataType, config);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // lenient（状态码不合法）：解析失败不抛，原样回字节——此时没有「文本」可回退。
                if (lenient)
                {
                    return bytes;
                }

                throw AxiosException.BadResponse(config, request, dataType, ex);
            }
        }

        var raw = Text();

        // 8) 基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）：框架直接解析文本，不依赖序列化器
        //    —— XML 的 <Age>30</Age> 与 JSON 的裸 42 都走这里；JSON 的带引号形态（"42"）交给序列化器兜底。
        if (ScalarParser.TryParse(raw, dataType, out var scalar))
        {
            return scalar;
        }

        // 9) 文本响应的业务对象：交给当前序列化器，返回什么对象，Data 就是什么。
        try
        {
            return AxiosContent.Get(config, mediaType).Deserialize(raw, dataType, config);
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
/// <para>
/// 存在意义：展开响应头要枚举响应头 + 内容头两套集合、逐个把头值装箱，还会触发 BCL 的惰性头值解析
/// （<c>Content-Type</c> → <c>MediaTypeHeaderValue</c> 等），实测每头约 0.2 µs；
/// 而绝大多数调用方从不读 <see cref="AxiosResponse.Headers"/>，这份开销原先每请求都要白付。
/// </para>
/// <para>
/// 约定保持不变：合并顺序仍是「响应头在前、内容头覆盖同名键」；
/// <see cref="AxiosResponseInfo.Headers"/> 与 <see cref="AxiosResponse.Headers"/> 仍是同一实例。
/// 头集合在 <c>HttpResponseMessage.Dispose()</c> 之后依然可读（释放的只是正文），
/// 所以视图可以安全地持有它们。
/// </para>
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

        // 并发首次访问可能重复构建一次，但只发布一份；不用锁是为了不把锁对象暴露给持有 Headers 的外部代码。
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
