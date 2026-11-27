using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet.Internal.Response;

/// <summary>响应阶段：把 <see cref="System.Net.Http.HttpResponseMessage"/> 转成 <see cref="AxiosResponse"/>，含响应头、正文读取、responseType 与目标类型分派、序列化器选型。</summary>
internal static class ResponseReader
{
    /// <summary>下载侧包装：按 <paramref name="declared"/> 预检 maxContentLength，再按需套进度 / 限速 / 长度上限流；都不需要时原样返回。</summary>
    /// <param name="stream">响应正文流。</param>
    /// <param name="declared">响应头声明的正文长度（<c>null</c> = 未声明，如 chunked）。</param>
    /// <param name="request">请求消息（错误上下文用）。</param>
    /// <param name="config">本次请求的生效配置。</param>
    private static Stream WrapDownloadStream(
        Stream stream,
        long? declared,
        HttpRequestMessage request,
        AxiosConfig config)
    {
        var limits = config.LimitsOrNull;
        var callbacks = config.ObservabilityOrNull;

        if (limits?.MaxResponseBytes is long limit && declared is long length && length > limit)
        {
            stream.Dispose();
            throw AxiosException.MaxResponseBytes(config, request, length, limit);
        }

        var maxRate = config.TransportOrNull?.MaxRate;

        if (callbacks?.OnDownloadProgress is null && maxRate is not > 0 && limits?.MaxResponseBytes is null)
        {
            return stream;
        }

        return new ProgressReadStream(
            stream,
            declared,
            callbacks?.OnDownloadProgress,
            maxRate,
            limits?.MaxResponseBytes,
            config,
            request);
    }

    /// <summary>段式累积的初始段大小（80 KB）。</summary>
    private const int InitialSegment = 81920;

    private const int MaxSegment = 8 * 1024 * 1024;

    /// <summary>按声明长度直接预分配的信任上限（8 MB），超过则走段式累积。</summary>
    private const long MaxPreallocated = 8 * 1024 * 1024;

    /// <summary>读取整个响应体：声明长度在信任上限内时按声明值预分配，否则段式累积；按真实字节数执行 maxContentLength。</summary>
    private static ValueTask<byte[]> ReadAllBytesAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        long? declaredLength)
        => declaredLength is > 0 and <= MaxPreallocated
            ? ReadDeclaredLengthAsync(stream, config, request, cancellationToken, (int)declaredLength.Value)
            : ReadSegmentedAsync(stream, config, request, cancellationToken, prefix: null);

    /// <summary>已知长度（≤ 信任上限）的读取：按声明值预分配，读满后再探一次确认没有尾部数据。</summary>
    // netstandard2.0 不支持方法级 AsyncMethodBuilder（CS0592）：仅影响状态机分配策略，不改语义。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<byte[]> ReadDeclaredLengthAsync(
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
                // 连接提前关闭：裁剪尾部。
                return Trim(buffer, filled);
            }

            filled += read;
        }

        // 再探一块确认正文结束。
        var probe = ArrayPool<byte>.Shared.Rent(InitialSegment);

        // 转交后由 ReadSegmentedAsync 归还。
        var handedOff = false;
        try
        {
            int more;
            try
            {
                more = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // 已读满，探测失败不影响交付。
                return buffer;
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // 归一到统一错误模型。
                throw AxiosException.Network(config, request, ex);
            }

            if (more == 0)
            {
                return buffer;
            }

            handedOff = true;
            return await ReadSegmentedAsync(stream, config, request, cancellationToken, prefix: buffer, current: probe, filled: more)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff)
            {
                ArrayPool<byte>.Shared.Return(probe);
            }
        }
    }

    /// <summary>段式累积：从 <see cref="ArrayPool{T}"/> 租段、倍增增长，收齐后装配一个精确数组（<paramref name="prefix"/> 不归还池）。</summary>
    // netstandard2.0 不支持方法级 AsyncMethodBuilder（CS0592）：仅影响状态机分配策略，不改语义。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<byte[]> ReadSegmentedAsync(
        Stream stream,
        AxiosConfig config,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        byte[]? prefix,
        byte[]? current = null,
        int filled = 0)
    {
        var segments = new List<byte[]>(4);

        // 前 ownedCount 段归调用方，不归还池。
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
                // 上限按真实字节数判，声明长度不参与。
                if (config.LimitsOrNull?.MaxResponseBytes is long limit && total > limit)
                {
                    throw AxiosException.MaxResponseBytes(config, request, total, limit);
                }

                if (filled == current.Length)
                {
                    // 读满一段：先租下一段再交出当前段。
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

            // 超过 PlatformCompat.ArrayMaxLength 时换成统一错误模型。
            if (total > PlatformCompat.ArrayMaxLength)
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

    /// <summary>读取状态码与响应头，装配 <see cref="AxiosConfig.Response"/> 要用的只读上下文；正文还没开始读。</summary>
    /// <remarks>调用方必须在写进 <see cref="AxiosConfig.Response"/> 之后再调用 <see cref="AxiosConfig.ValidateStatus"/>，委托里才读得到状态码与响应头。</remarks>
    internal static AxiosResponseInfo ReadInfo(HttpResponseMessage message)
    {
        var status = (int)message.StatusCode;
        var headers = new ResponseHeadersView(message.Headers, message.Content.Headers);
        return new AxiosResponseInfo(status, StatusTextMap.Get(status), headers, ContentTypeHeader.ReadRaw(message.Content.Headers));
    }

    /// <summary>读响应并转成 <see cref="AxiosResponse"/>：状态码与响应头 → 正文 → 按 responseType 与目标类型分派为 Data。</summary>
    /// <remarks><c>info</c> 是调用方用 <see cref="ReadInfo"/> 建好、并已写进 <see cref="AxiosConfig.Response"/> 的响应上下文，必须与本次 <c>message</c> 同源。</remarks>
    // netstandard2.0 不支持方法级 AsyncMethodBuilder（CS0592）：仅影响状态机分配策略，不改语义。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    public static async ValueTask<AxiosResponse> ReadAsync(
        HttpResponseMessage message,
        HttpRequestMessage request,
        AxiosConfig config,
        CancellationToken cancellationToken,
        Type? dataType,
        AxiosResponse? target,
        bool lenient,
        AxiosResponseInfo info)
    {
        var headers = info.Headers;
        var status = info.Status;
        var statusText = info.StatusText;

        // Content-Type 只留原始值，媒体类型 / 字符集按需解析。
        var rawContentType = info.RawContentType;

        object? data;
        // 已解码正文（二进制 / 流为 null）。
        string? rawText = null;
        // 正文字节数；流式路径为 null。
        long? bodyBytes = null;
        // 原始正文字节，启用缓存时留给管线。
        byte[]? rawBody = null;
        try
        {
            if (config.ResponseType == ResponseType.Stream)
            {
                var responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                data = WrapDownloadStream(
                    responseStream,
                    message.Content.Headers.ContentLength,
                    request,
                    config);
            }
            else
            {
                Stream responseStream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                // 声明长度：预检、进度流期望总量与预分配容量共用。
                var declared = message.Content.Headers.ContentLength;
                var downloadStream = WrapDownloadStream(responseStream, declared, request, config);
                byte[] bytes;
                try
                {
                    bytes = await ReadAllBytesAsync(downloadStream, config, request, cancellationToken, declared).ConfigureAwait(false);
                }
                finally
                {
                    // 这一层负责释放，读失败同样要还连接。
                    downloadStream.Dispose();
                }

                bodyBytes = bytes.Length;
                rawBody = bytes;

                // 原始字节钩子：在解码 / 反序列化之前触发；回调期间零拷贝借用，需长期持有请自行复制。
                if (config.ObservabilityOrNull?.OnRawResponse is { } onRawResponse)
                {
                    try
                    {
                        onRawResponse(config.Response!, bytes.AsMemory());
                    }
                    catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
                    {
                        throw AxiosException.CallbackFailed(config, request, nameof(AxiosConfig.ObservabilitySettings.OnRawResponse), ex, CallbackStage.Response);
                    }
                }

                try
                {
                    data = ConvertBody(
                        config,
                        request,
                        dataType,
                        bytes,
                        rawContentType,
                        lenient,
                        out rawText);
                }
                catch (AxiosException ex) when (ex.Response is null)
                {
                    // 反序列化与 transformResponse 抛出的 AxiosException 一律补上下文：
                    // 调用方自定义错误码（非 ERR_BAD_RESPONSE）时错误码保持不变，但状态码 / 响应头 / 原文不能丢。
                    AttachResponseContext(ex, config, request, target, status, statusText, message.Version, headers, rawText, rawBody, bodyBytes);
                    throw;
                }
            }
        }
        catch (AxiosException ex) when (ex.Code == AxiosException.ErrBadResponse && ex.Response is null)
        {
            // 状态码与响应头已到手：错误必须能读到响应上下文。
            AttachResponseContext(ex, config, request, target, status, statusText, message.Version, headers, rawText, rawBody, bodyBytes);
            throw;
        }

        // 目标响应对象就地填充。
        var response = target ?? new AxiosResponse();
        response.Data = data;

        response.Status = status;
        response.StatusText = statusText;
        response.ProtocolVersion = message.Version;
        response.BodyBytes = bodyBytes;

        // 字节先落（会清掉上一轮尝试残留的文本），错误原文再覆盖。
        ApplyRawBody(response, rawBody, config, lenient, data);
        if (lenient)
        {
            response.RawText = rawText;
        }

        response.Headers = headers;
        response.Config = config;
        response.Request = request;
        return response;
    }

    /// <summary>装配失败时把响应上下文（状态码、响应头、原文）补进错误；网络与短路路径共用。</summary>
    /// <remarks><paramref name="ownedBytes"/> 为真表示字节本来就被这次响应持有（短路 / 缓存），不受错误体上限限制。</remarks>
    private static void AttachResponseContext(
        AxiosException exception,
        AxiosConfig config,
        HttpRequestMessage? request,
        AxiosResponse? target,
        int status,
        string statusText,
        Version? protocolVersion,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string? rawText,
        byte[]? rawBytes,
        long? bodyBytes,
        bool ownedBytes = false)
    {
        var failed = target ?? new AxiosResponse();
        failed.Data = null;
        failed.Status = status;
        failed.StatusText = statusText;
        failed.ProtocolVersion = protocolVersion;
        failed.BodyBytes = bodyBytes;
        failed.Headers = headers;
        failed.Config = config;
        failed.Request = request;

        // 目标对象可能被重试复用，正文相关字段都要重写；文本是错误消息的输入，必须在字节之后写。
        ApplyRawBody(failed, rawBytes, config, error: true, owned: ownedBytes);
        failed.RawText = rawText;
        exception.Response = failed;
    }

    /// <summary>错误响应留给 <see cref="AxiosResponse.RawBody"/> 的字节上限：超过只留文本（<see cref="AxiosResponse.RawText"/> 始终完整）。</summary>
    private const int ErrorRawBodyLimit = 64 * 1024;

    /// <summary>把正文缓冲与只读视图写进响应。</summary>
    /// <param name="response">目标响应（重试时可能复用）。</param>
    /// <param name="rawBytes">本次尝试读到的完整字节，流式路径为 <c>null</c>。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="error">这条响应是否按错误交付（决定是否主动保留字节）。</param>
    /// <param name="data">已装配的正文；<c>byte[]</c> 形态时与 <paramref name="rawBytes"/> 是同一块数组。</param>
    /// <param name="owned">字节本来就被这次响应持有（短路 / 缓存的正文），保留视图不增加常驻内存。</param>
    private static void ApplyRawBody(
        AxiosResponse response,
        byte[]? rawBytes,
        AxiosConfig config,
        bool error,
        object? data = null,
        bool owned = false)
    {
        response.RawBodyBuffer = config.CacheCaptureRawBody ? rawBytes : null;

        // 保留条件：字节这次响应本来就要持有（短路 / 缓存的正文、byte[] 形态的 Data、阈值内的错误体），或调用方显式要求。
        var retain = rawBytes is not null
                     && (owned
                         || ReferenceEquals(rawBytes, data)
                         || (error && rawBytes.Length <= ErrorRawBodyLimit));

        SetRawBodyView(response, rawBytes, config, retain);
    }

    /// <summary>设置只读原始字节视图：<paramref name="retain"/> 为真或调用方显式开启时才有值。</summary>
    private static void SetRawBodyView(AxiosResponse response, byte[]? rawBytes, AxiosConfig config, bool retain)
    {
        if (rawBytes is null
            || !(retain || config.ObservabilityOrNull?.CaptureRawResponseBody is true))
        {
            response.SetRawBody(null, null, null);
            return;
        }

        response.SetRawBody(
            rawBytes.AsMemory(),
            config.Response?.CharSet,
            config.SerializationOrNull?.TextEncoding);
    }

    /// <summary>是否需要把响应字节解码成文本。</summary>
    private static bool NeedsText(AxiosConfig config, Type? dataType, bool textual) => config.ResponseType switch
    {
        ResponseType.ArrayBuffer => false,
        ResponseType.Stream => false,
        ResponseType.Text or ResponseType.Dynamic => true,
        _ => textual && dataType != typeof(byte[])
    };

    /// <summary>body 是否只含空白字符。</summary>
    private static bool IsBlank(string text) => text.AsSpan().Trim().IsEmpty;

    /// <summary>首个非空白字符是否可能是 JSON 起始符（<c>\uFEFF</c> 按空白跳过）。</summary>
    private static bool LooksLikeJson(string text)
    {
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\uFEFF')
            {
                continue;
            }

            return IsJsonStart(c);
        }

        return false;
    }

    /// <summary>UTF-8 版本；BOM 由调用方剥掉。</summary>
    private static bool LooksLikeJson(ReadOnlySpan<byte> json)
    {
        foreach (var b in json)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return IsJsonStart((char)b);
        }

        return false;
    }

    /// <summary>JSON 起始字符：对象、数组、字符串、负号、字面量首字母与数字。</summary>
    private static bool IsJsonStart(char c)
        => c is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || (c >= '0' && c <= '9');

    /// <summary>未解码时的空白判定：只认 ASCII 空白，先跳过 UTF-8 BOM。</summary>
    private static bool IsBlankUtf8(ReadOnlySpan<byte> bytes)
    {
        if (BomDetection.HasUtf8Bom(bytes))
        {
            bytes = bytes[3..];
        }

        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>从原始字节装配 Data：解码 → 按 responseType / 目标类型转换 → transformResponse；<paramref name="rawContentType"/> 是原始头值。</summary>
    internal static object? ConvertBody(
        AxiosConfig config,
        HttpRequestMessage? request,
        Type? dataType,
        byte[] bytes,
        string? rawContentType,
        bool lenient,
        out string? rawText)
    {
        string? text = null;

        // 媒体类型 / 文本性 / 字符集均惰性解析。
        string? mediaType = null;
        var mediaTypeResolved = false;

        string? GetMediaType()
        {
            if (!mediaTypeResolved)
            {
                mediaType = ContentTypeHeader.MediaTypeOf(rawContentType);
                mediaTypeResolved = true;
            }

            return mediaType;
        }

        // 文本性按解析出的主值判定；头解析不出时按文本处理。
        var textual = false;
        var textualResolved = false;

        bool IsTextual()
        {
            if (!textualResolved)
            {
                textual = MediaTypes.IsTextual(GetMediaType());
                textualResolved = true;
            }

            return textual;
        }

        // 字符集同样惰性解析。
        string? charSet = null;
        var charSetResolved = false;

        string? GetCharSet()
        {
            if (!charSetResolved)
            {
                charSet = ContentTypeHeader.CharSetOf(rawContentType);
                charSetResolved = true;
            }

            return charSet;
        }

        // 需要文本时补齐解码（只解一次）。
        string? EnsureText()
        {
            if (text is null && NeedsText(config, dataType, IsTextual()))
            {
                text = ResponseTextDecoder.Decode(bytes, GetCharSet(), config.SerializationOrNull?.TextEncoding);
            }

            return text;
        }

        object? data;
        try
        {
            data = ConvertData(config, request, dataType, bytes, ref text, rawContentType, lenient);
        }
        catch
        {
            // 解析失败也要把原文交回异常响应。
            rawText = EnsureText();
            throw;
        }

        if (lenient)
        {
            EnsureText();
        }

        rawText = text;

        // transformResponse：反序列化之后依次加工 Data；普通异常归一到 ERR_BAD_RESPONSE。
        if (config.TransformResponseSnapshot is { } responseTransforms)
        {
            foreach (var transform in responseTransforms)
            {
                try
                {
                    data = transform(data, config);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 转换失败时同样把原文补进异常响应。
                    rawText = EnsureText();

                    // 调用方自己抛的 AxiosException 原样上抛（保留自定义错误码），
                    // 响应上下文由调用方按 Response 为 null 时补；普通异常在这里归一。
                    if (ex is AxiosException)
                    {
                        throw;
                    }

                    throw AxiosException.ResponseTransformFailed(config, request, ex);
                }
            }
        }

        return data;
    }

    /// <summary>短路响应的装配：语义与 <see cref="ReadAsync"/> 一致，正文不来自网络；<see cref="AxiosShortCircuit.Data"/> 原样交付（不跑 transformResponse）。</summary>
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

        // Body 与 Data 互斥。
        if (reply.Body is not null && reply.Data is not null)
        {
            throw AxiosException.InvalidOption(
                "AxiosShortCircuit 不能同时设置 Body 与 Data：Body 是原始正文（按本次调用的类型重新解析），"
                + "Data 是已成型的结果（原样交付），请二选一。", config);
        }

        var statusText = reply.StatusText ?? StatusTextMap.Get(status);
        var headers = BuildShortCircuitHeaders(reply, config);

        // 与网络路径同一份响应上下文。
        var rawContentType = ReadRawContentType(headers);
        config.Response = new AxiosResponseInfo(status, statusText, headers, rawContentType);

        object? data;
        string? rawText = null;
        long? bodyBytes = null;

        if (reply.Body is { } body)
        {
            try
            {
                // 体积上限同样生效。
                if (config.LimitsOrNull?.MaxResponseBytes is long maxLength && body.Length > maxLength)
                {
                    throw AxiosException.MaxResponseBytes(config, null, body.Length, maxLength);
                }

                bodyBytes = body.Length;

                if (config.ResponseType == ResponseType.Stream)
                {
                    // 流式正文必须复制底层字节。
                    data = new MemoryStream((byte[])body.Clone(), writable: false);
                }
                else
                {
                    try
                    {
                        data = ConvertBody(config, null, dataType, body, rawContentType, lenient, out rawText);
                    }
                    catch (AxiosException ex) when (ex.Response is null)
                    {
                        // 与网络路径同一口径：transformResponse 抛自定义 AxiosException 时也要带响应上下文。
                        AttachResponseContext(ex, config, null, target, status, statusText, reply.ProtocolVersion, headers, rawText, reply.Body, bodyBytes, ownedBytes: true);
                        throw;
                    }

                    // 字节形态必须复制。
                    if (ReferenceEquals(data, body))
                    {
                        data = (byte[])body.Clone();
                    }
                }
            }
            catch (AxiosException ex) when (ex.Code == AxiosException.ErrBadResponse && ex.Response is null)
            {
                // 超限与解析失败同样补响应上下文；短路正文由调用方提供，不受错误体上限限制。
                AttachResponseContext(ex, config, null, target, status, statusText, reply.ProtocolVersion, headers, rawText, reply.Body, bodyBytes, ownedBytes: true);
                throw;
            }
        }
        else
        {
            data = reply.Data;

            // 只有字符串能充当错误原文。
            rawText = data as string;
        }

        var response = target ?? new AxiosResponse();
        response.Data = data;
        response.Status = status;
        response.StatusText = statusText;
        response.ProtocolVersion = reply.ProtocolVersion;
        response.BodyBytes = bodyBytes;

        // 短路正文由调用方提供，本来就在内存里，视图恒给；字节先落，错误原文再覆盖。
        // Data 直接给 byte[] 时字节同样在手上，与网络路径的别名规则一致。
        var ownedBytes = reply.Body ?? reply.Data as byte[];
        SetRawBodyView(response, ownedBytes, config, retain: ownedBytes is not null);
        if (lenient)
        {
            response.RawText = rawText;
        }

        response.Headers = headers;
        response.Config = config;

        response.Request = null;
        return response;
    }

    /// <summary>短路响应头：搬进大小写不敏感的只读字典，<see cref="AxiosShortCircuit.ContentType"/> 覆盖同名项；非法头归一到 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    private static Dictionary<string, IEnumerable<string>> BuildShortCircuitHeaders(
        AxiosShortCircuit reply,
        AxiosConfig config)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 多值头优先；每个值都非 null。
            if (reply.MultiValueHeaders is { Count: > 0 } multi)
            {
                foreach (var header in multi)
                {
                    if (string.IsNullOrEmpty(header.Key))
                    {
                        continue;
                    }

                    var name = AxiosHeaders.ValidateName(header.Key);
                    var values = new List<string>(2);
                    foreach (var value in header.Value)
                    {
                        if (value is null)
                        {
                            continue;
                        }

                        AxiosHeaders.ValidateHeaderValue(name, value);
                        values.Add(value);
                    }

                    if (values.Count > 0)
                    {
                        headers[name] = values;
                    }
                }
            }
            else if (reply.Headers is { Count: > 0 } source)
            {
                foreach (var header in source)
                {
                    // 空头名 / null 值静默跳过。
                    if (string.IsNullOrEmpty(header.Key) || header.Value is null)
                    {
                        continue;
                    }

                    // 与请求头同一套校验。
                    var name = AxiosHeaders.ValidateName(header.Key);
                    AxiosHeaders.ValidateHeaderValue(name, header.Value);
                    headers[name] = new[] { header.Value };
                }
            }

            if (reply.ContentType is { Length: > 0 } contentType)
            {
                AxiosHeaders.ValidateHeaderValue("Content-Type", contentType);
                headers["Content-Type"] = new[] { contentType };
            }

            // 缓存命中：Age = 缓存龄期 + 在缓存中停留的时间。
            if (reply.ReceivedAt is { } receivedAt && reply.StoredAge is { } storedAge && headers.ContainsKey("Age"))
            {
                var seconds = (long)(storedAge + (DateTimeOffset.UtcNow - receivedAt)).TotalSeconds;
                headers["Age"] = new[] { (seconds > 0 ? seconds : 0).ToString(CultureInfo.InvariantCulture) };
            }
        }
        catch (ArgumentException ex)
        {
            throw AxiosException.InvalidOption($"短路响应的响应头非法：{ex.Message}", config);
        }

        return headers;
    }

    /// <summary>从响应头字典取 <c>Content-Type</c> 的原始值。</summary>
    private static string? ReadRawContentType(IReadOnlyDictionary<string, IEnumerable<string>> headers)
        => headers.TryGetValue("Content-Type", out var values) ? values.FirstOrDefault() : null;

    /// <summary>按 responseType / 目标类型把原始字节转换为 Data；<paramref name="text"/> 是解码缓存。</summary>
    private static object? ConvertData(
        AxiosConfig config,
        HttpRequestMessage? request,
        Type? dataType,
        byte[] bytes,
        ref string? text,
        string? rawContentType,
        bool lenient)
    {
        var state = new Conversion(config, request, bytes, rawContentType, lenient);
        state.Text = text;

        try
        {
            return ConvertCore(dataType, ref state);
        }
        finally
        {
            text = state.Text;
        }
    }

    /// <summary>按 responseType 与目标类型分派转换分支；顺序即优先级，未命中的走业务对象路径。</summary>
    private static object? ConvertCore(Type? dataType, ref Conversion state)
    {
        // 0) 调用方不要正文：请求照发，正文不解析。
        if (dataType == typeof(void))
        {
            return null;
        }

        // 1) 显式 responseType 优先。
        switch (state.Config.ResponseType)
        {
            case ResponseType.ArrayBuffer:
                return state.Bytes;

            case ResponseType.Text:
                return state.DecodeText();

            case ResponseType.Dynamic:
                return ConvertDynamic(ref state);
        }

        // 4) 目标类型就是原始内容本身。
        if (dataType == typeof(string))
        {
            return state.DecodeText();
        }

        if (dataType == typeof(byte[]))
        {
            return state.Bytes;
        }

        // 5) 空 body 与只含空白的 body 都视为 null。
        if (IsBlankUtf8(state.Bytes))
        {
            return null;
        }

        // 6) 无目标类型：框架按 JSON 文档形态给出，解析失败原样返回文本。
        if (dataType is null || dataType == typeof(object) || dataType == typeof(JsonElement))
        {
            return ConvertDocument(ref state);
        }

        // 7) 二进制响应：交给序列化器的字节入口。
        if (!state.IsTextual())
        {
            return ConvertBinary(dataType, ref state);
        }

        // 8) 基础类型：框架直接解析文本。
        if (ScalarTypes.IsScalarType(dataType) || typeof(IConvertible).IsAssignableFrom(dataType))
        {
            return ConvertScalar(dataType, ref state);
        }

        // 9) 业务对象：UTF-8 走序列化器字节入口，其它编码解码后走文本入口。
        return ConvertObject(dataType, ref state);
    }

    /// <summary>dynamic 分支：空正文给 <c>null</c>，UTF-8 按字节解析，失败按解码文本再试一次。</summary>
    private static object? ConvertDynamic(ref Conversion state)
    {
        var bytes = state.Bytes;
        var config = state.Config;
        var request = state.Request;

        if (IsBlankUtf8(bytes))
        {
            return null;
        }

        // UTF-8 按字节解析，其它编码按解码文本解析。
        var utf8 = IsUtf8Bytes(bytes, state.GetCharSet(), config);

        try
        {
            return utf8
                ? DynamicJson.Parse(StripUtf8Bom(bytes))
                : DynamicJson.Parse(state.DecodeText());
        }
        catch (Exception ex) when (ex is not AxiosException)
        {
            // 非 ASCII 空白（如全角空格）仍视为 null。
            var json = state.DecodeText();
            if (IsBlank(json))
            {
                return null;
            }

            // 字节解析失败时按解码文本再试一次（坏字节替换为 U+FFFD）。
            if (utf8)
            {
                try
                {
                    return DynamicJson.Parse(json);
                }
                catch (Exception retryEx) when (retryEx is not AxiosException)
                {
                    ex = retryEx;
                }
            }

            // 非 JSON 响应走统一错误模型，原文一并带上。
            if (state.Lenient)
            {
                return json;
            }

            throw AxiosException.BadResponse(config, request, "dynamic JSON", ex, json);
        }
    }

    /// <summary>无目标类型分支：二进制给字节，文本按 JSON 文档形态解析，解析失败原样返回文本。</summary>
    private static object? ConvertDocument(ref Conversion state)
    {
        var bytes = state.Bytes;
        var config = state.Config;

        // 二进制响应原样给字节。
        if (!state.IsTextual())
        {
            return bytes;
        }

        // UTF-8 字节直接解析；非法 UTF-8 交给下面的文本路径。
        if (IsUtf8Bytes(bytes, state.GetCharSet(), config))
        {
            var json = StripUtf8Bom(bytes);

            // 起始符不像 JSON 时直接返回原文。
            if (LooksLikeJson(json.Span) && ResponseTextDecoder.IsValidUtf8(json.Span))
            {
                try
                {
                    // Clone 后即可释放文档。
                    using var parsed = JsonDocument.Parse(json);
                    return parsed.RootElement.Clone();
                }
                catch (Exception ex) when (ex is not AxiosException)
                {
                    // 语法错误：落到下面的文本路径。
                }
            }
        }

        var document = state.DecodeText();

        if (!LooksLikeJson(document))
        {
            return NonBlankOrNull(document);
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

    /// <summary>二进制响应分支：不解码，交给序列化器字节入口；lenient 时解析失败回退原始字节。</summary>
    private static object? ConvertBinary(Type dataType, ref Conversion state)
    {
        try
        {
            return AxiosContent.Get(state.Config, state.GetMediaType()).Deserialize(state.Bytes, dataType, state.Config);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            // lenient：回退原始字节。
            if (state.Lenient)
            {
                return state.Bytes;
            }

            throw AxiosException.BadResponse(state.Config, state.Request, dataType, ex);
        }
    }

    /// <summary>基础类型分支：先按文本直接解析，失败再交序列化器兜底；lenient 时回退原文。</summary>
    private static object? ConvertScalar(Type dataType, ref Conversion state)
    {
        var config = state.Config;
        var request = state.Request;
        var raw = state.DecodeText();

        // 解码后仍为空白的正文视为 null（字节级判空认不出非 ASCII 空白）。
        if (IsBlank(raw))
        {
            return null;
        }

        if (ScalarParser.TryParse(raw, dataType, out var scalar))
        {
            return scalar;
        }

        try
        {
            var parsed = AxiosContent.Get(config, state.GetMediaType()).Deserialize(raw, dataType, config);

            // 序列化器不复核枚举已定义值。
            if (parsed is Enum enumValue && !ScalarParser.IsAcceptableEnumValue(dataType, enumValue))
            {
                // lenient：回退原文。
                if (state.Lenient)
                {
                    return raw;
                }

                // 可能为 Nullable，消息里报底层枚举类型。
                throw AxiosException.UndefinedEnumValue(config, request, (Nullable.GetUnderlyingType(dataType) ?? dataType).Name, enumValue.ToString());
            }

            return parsed;
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            // 非 ASCII 空白视为 null。
            if (IsBlank(raw))
            {
                return null;
            }

            if (state.Lenient)
            {
                return raw;
            }

            // 解析失败把原文带进消息。
            throw AxiosException.BadResponse(config, request, dataType, ex, raw);
        }
    }

    /// <summary>业务对象分支：UTF-8 走序列化器字节入口，其它编码解码后走文本入口；lenient 时回退原文。</summary>
    private static object? ConvertObject(Type dataType, ref Conversion state)
    {
        var config = state.Config;
        var request = state.Request;
        var bytes = state.Bytes;

        try
        {
            var serializer = AxiosContent.Get(config, state.GetMediaType());
            object? deserialized;

            // 非法 UTF-8 不进字节入口，改走解码文本。
            if (IsUtf8Bytes(bytes, state.GetCharSet(), config) && ResponseTextDecoder.IsValidUtf8(bytes))
            {
                deserialized = serializer.Deserialize(StripUtf8Bom(bytes), dataType, config);
            }
            else
            {
                deserialized = serializer.Deserialize(state.DecodeText(), dataType, config);
            }

            // 序列化器不复核枚举已定义值。
            if (deserialized is Enum enumValue && !ScalarParser.IsAcceptableEnumValue(dataType, enumValue))
            {
                // lenient：回退原文。
                if (state.Lenient)
                {
                    return state.DecodeText();
                }

                // 可能为 Nullable，消息里报底层枚举类型。
                throw AxiosException.UndefinedEnumValue(config, request, (Nullable.GetUnderlyingType(dataType) ?? dataType).Name, enumValue.ToString());
            }

            return deserialized;
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            var fallback = state.DecodeText();

            // 非 ASCII 空白视为 null。
            if (IsBlank(fallback))
            {
                return null;
            }

            if (state.Lenient)
            {
                return fallback;
            }

            // 解析失败把正文片段带进消息。
            throw AxiosException.BadResponse(config, request, dataType, ex, fallback);
        }
    }

    /// <summary>解析失败后的原文回退：只含空白的正文返回 <c>null</c>。</summary>
    private static string? NonBlankOrNull(string text) => IsBlank(text) ? null : text;

    /// <summary>一次正文转换的输入与惰性解析缓存（媒体类型 / 文本性 / 字符集 / 解码结果）。</summary>
    private struct Conversion
    {
        /// <summary>字符集是否已解析。</summary>
        private bool _charSetResolved;

        /// <summary>解析出的字符集，未声明为 <c>null</c>。</summary>
        private string? _charSet;

        /// <summary>媒体类型主值是否已解析。</summary>
        private bool _mediaTypeResolved;

        /// <summary>解析出的媒体类型主值，未带或非法 <c>Content-Type</c> 为 <c>null</c>。</summary>
        private string? _mediaType;

        /// <summary>文本性是否已判定。</summary>
        private bool _textualResolved;

        /// <summary>文本性判定结果。</summary>
        private bool _textual;

        /// <summary>按本次转换的输入初始化，缓存标记置为未解析。</summary>
        public Conversion(AxiosConfig config, HttpRequestMessage? request, byte[] bytes, string? rawContentType, bool lenient)
        {
            Config = config;
            Request = request;
            Bytes = bytes;
            RawContentType = rawContentType;
            Lenient = lenient;
            Text = null;
            _charSetResolved = false;
            _charSet = null;
            _mediaTypeResolved = false;
            _mediaType = null;
            _textualResolved = false;
            _textual = false;
        }

        public AxiosConfig Config { get; }

        /// <summary>请求消息（仅错误上下文）；短路响应为 <c>null</c>。</summary>
        public HttpRequestMessage? Request { get; }

        public byte[] Bytes { get; }

        /// <summary>响应头里的原始 <c>Content-Type</c>，未带时为 <c>null</c>。</summary>
        public string? RawContentType { get; }

        /// <summary>解析失败是否回退原文；错误响应路径为 <c>true</c>。</summary>
        public bool Lenient { get; }

        /// <summary>解码缓存，只有需要文本的分支才填。</summary>
        public string? Text { get; set; }

        /// <summary>媒体类型主值；未带或非法的 <c>Content-Type</c> 解析为 <c>null</c>。</summary>
        public string? GetMediaType()
        {
            if (!_mediaTypeResolved)
            {
                _mediaType = ContentTypeHeader.MediaTypeOf(RawContentType);
                _mediaTypeResolved = true;
            }

            return _mediaType;
        }

        /// <summary>是否文本类型；头解析不出时按文本处理。</summary>
        public bool IsTextual()
        {
            if (!_textualResolved)
            {
                _textual = MediaTypes.IsTextual(GetMediaType());
                _textualResolved = true;
            }

            return _textual;
        }

        /// <summary>声明的字符集；未声明 <c>charset</c> 时为 <c>null</c>。</summary>
        public string? GetCharSet()
        {
            if (!_charSetResolved)
            {
                _charSet = ContentTypeHeader.CharSetOf(RawContentType);
                _charSetResolved = true;
            }

            return _charSet;
        }

        /// <summary>按需解码正文（只解一次）并写入缓存。</summary>
        public string DecodeText()
            => Text ??= ResponseTextDecoder.Decode(Bytes, GetCharSet(), Config.SerializationOrNull?.TextEncoding);
    }

    /// <summary>字节能否直接按 UTF-8 解析（未强制其它编码，且开头不是 UTF-16 / UTF-32 BOM）。</summary>
    private static bool IsUtf8Bytes(byte[] bytes, string? charSet, AxiosConfig config)
    {
        if (!ResponseTextDecoder.IsUtf8(charSet, config.SerializationOrNull?.TextEncoding))
        {
            return false;
        }

        return ResponseTextDecoder.DetectFromBom(bytes) is not { } bom || bom.CodePage == Encoding.UTF8.CodePage;
    }

    /// <summary>剥掉开头的 UTF-8 BOM（JSON 字节入口不接受 BOM）。</summary>
    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] bytes)
        => BomDetection.HasUtf8Bom(bytes)
            ? bytes.AsMemory(3)
            : bytes;
}

/// <summary>响应头的惰性只读视图：首次访问时才合并两套头集合（内容头覆盖同名响应头）；<c>HttpResponseMessage.Dispose()</c> 之后仍可读。</summary>
internal sealed class ResponseHeadersView : IReadOnlyDictionary<string, IEnumerable<string>>
{
    private readonly HttpHeaders? _responseHeaders;
    private readonly HttpContentHeaders? _contentHeaders;
    // 展开后的头字典（懒建一次）。
    private Dictionary<string, IEnumerable<string>>? _map;

    public ResponseHeadersView(HttpHeaders? responseHeaders, HttpContentHeaders? contentHeaders)
    {
        _responseHeaders = responseHeaders;
        _contentHeaders = contentHeaders;
    }

    private Dictionary<string, IEnumerable<string>> Map => Volatile.Read(ref _map) ?? Build();

    /// <summary>合并两套头集合（内容头覆盖同名响应头）；并发首次访问只发布一份。</summary>
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

        // 并发首次访问可能重复构建，只发布一份。
        return Interlocked.CompareExchange(ref _map, map, null) ?? map;
    }

    public IEnumerable<string> this[string key] => Map[key];

    public IEnumerable<string> Keys => Map.Keys;

    public IEnumerable<IEnumerable<string>> Values => Map.Values;

    public int Count => Map.Count;

    /// <summary>是否含指定响应头（不区分大小写）。</summary>
    public bool ContainsKey(string key) => Map.ContainsKey(key);

    /// <summary>取指定响应头的全部值；不存在时返回 false。</summary>
    public bool TryGetValue(string key, out IEnumerable<string> value) => Map.TryGetValue(key, out value!);

    /// <summary>枚举合并后的全部响应头。</summary>
    public IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumerator() => Map.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
