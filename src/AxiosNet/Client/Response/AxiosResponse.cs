
namespace AxiosNet;

/// <summary>
/// 非泛型响应，对应 axios response 结构：
/// data / status / statusText / headers / config / request。
/// </summary>
public class AxiosResponse
{
    /// <summary>已解码的原文；为 <c>null</c> 且 <see cref="_rawBody"/> 有值时按需解码。</summary>
    private string? _rawText;

    /// <summary>原始正文字节视图，<see cref="RawBody"/> 的存储。</summary>
    private ReadOnlyMemory<byte>? _rawBody;

    /// <summary>派生 <see cref="_rawText"/> 时使用的响应 <c>charset</c>。</summary>
    private string? _rawBodyCharSet;

    /// <summary>派生 <see cref="_rawText"/> 时使用的显式编码，优先于 <see cref="_rawBodyCharSet"/>。</summary>
    private Encoding? _rawBodyEncoding;

    /// <summary>响应体（已按 responseType 反序列化）。</summary>
    public object? Data { get; set; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; set; }

    /// <summary>状态码对应的标准文案。</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>这次请求实际协商到的 HTTP 版本（<c>1.1</c> / <c>2.0</c> / <c>3.0</c>）。</summary>
    public Version? ProtocolVersion { get; set; }

    private IReadOnlyDictionary<string, IEnumerable<string>>? _headers;

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感）。</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers
    {
        get => _headers ??= new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        set => _headers = value;
    }

    /// <summary>触发本次请求的生效配置（实例默认值 + 本次请求项已合并）。</summary>
    /// <remarks>交回 <see cref="AxiosClient.RequestAsync(AxiosConfig)"/> 时按「按请求项」合并；实例级项取目标实例的值。</remarks>
    public AxiosConfig? Config { get; set; }

    /// <summary>响应正文的原文（字符串形态）：由 <see cref="RawBody"/> 按 charset 解码，首次读取时才解码。</summary>
    /// <remarks>
    /// 失败响应一定有值（状态码被 <see cref="AxiosConfig.ValidateStatus"/> 拒绝或正文解析失败）；
    /// 成功响应在 <see cref="RawBody"/> 有值时同样可读。
    /// 二进制与网络流式成功响应没有完整字节，为 <c>null</c>。
    /// </remarks>
    public string? RawText
    {
        get => _rawText ??= _rawBody is { } body
            ? ResponseTextDecoder.Decode(body.Span, _rawBodyCharSet, _rawBodyEncoding)
            : null;
        internal set => _rawText = value;
    }

    /// <summary>底层 HttpRequestMessage（对应 axios response.request），请求结束后已被释放。</summary>
    /// <remarks><c>Method</c> / <c>RequestUri</c> / <c>Headers</c> 仍可读；<c>Content</c> 不可依赖，读取只会得到 <c>null</c> 或已释放对象。</remarks>
    public HttpRequestMessage? Request { get; set; }

    /// <summary>便捷获取响应头第一个值。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>便捷获取<b>请求</b>头第一个值；短路响应无底层请求时返回 <c>null</c>。</summary>
    /// <remarks>先查请求头，再查内容头；重试过的请求正文已摘掉，此时内容头读不到。</remarks>
    public string? GetRequestHeader(string name)
    {
        if (Request is not { } request)
        {
            return null;
        }

        if (request.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return request.Content?.Headers.TryGetValues(name, out var contentValues) is true
            ? contentValues.FirstOrDefault()
            : null;
    }

    /// <summary>响应正文字节数（只有非流式响应有值），供遥测写进 span。</summary>
    /// <remarks>
    /// 是<b>长度</b>不是字节本身：字节内容看 <see cref="RawBody"/> 或 <see cref="RawBodyBuffer"/>（都是 <c>byte[]</c> 形态）。
    /// <see cref="AxiosConfig.ResponseType"/> 为 <c>Stream</c> 时无值（<c>null</c>）；空正文是 <c>0</c>。长度取自实际读到的字节，不用 <c>Content-Length</c>。
    /// </remarks>
    internal long? BodyBytes { get; set; }

    /// <summary>这次响应是条件请求的 304 合并出来的（正文来自缓存），供遥测与日志区分「未命中」。</summary>
    internal bool CacheRevalidated { get; set; }

    /// <summary>响应正文的原始字节（只读视图）：已解压、未解码。</summary>
    /// <remarks>
    /// 有值当且仅当这次响应本来就持有完整字节（错误响应、短路 / 缓存命中、<c>byte[]</c> 形态的 Data），
    /// 或打开 <see cref="AxiosConfig.ObservabilitySettings.CaptureRawResponseBody"/>。
    /// <c>byte[]</c> 形态的 Data 与它同数组，改 Data 等于改这里；网络流式成功响应没有完整字节；空正文是长度 0 而非 <c>null</c>。
    /// </remarks>
    public ReadOnlyMemory<byte>? RawBody
    {
        get => _rawBody;
        internal set => SetRawBody(value, null, null);
    }

    /// <summary>写入原始字节与解码口径；换字节时作废已解码的 <see cref="RawText"/>。</summary>
    /// <param name="body">原始正文字节视图，<c>null</c> 表示不留。</param>
    /// <param name="charSet">响应声明的 <c>charset</c>，派生 <see cref="RawText"/> 时使用。</param>
    /// <param name="encoding">显式的文本编码，优先于 <paramref name="charSet"/>。</param>
    internal void SetRawBody(ReadOnlyMemory<byte>? body, string? charSet, Encoding? encoding)
    {
        _rawBody = body;
        _rawBodyCharSet = charSet;
        _rawBodyEncoding = encoding;
        _rawText = null;
    }

    /// <summary>缓存落库用的正文缓冲：与 <see cref="RawBody"/> 指向同一块数组，落库或失败收尾后清空。</summary>
    internal byte[]? RawBodyBuffer { get; set; }

    /// <summary>复制一份当前状态的响应，供异常对象长期持有。</summary>
    /// <remarks>
    /// 备用端点会就地改写同一个响应对象（状态码、正文、请求地址逐个覆盖），异常却要保留「它失败那一刻」的快照，
    /// 否则一个异常里会同时出现首端点的消息与尾端点的状态码。只在失败路径调用。
    /// </remarks>
    internal AxiosResponse CopyForErrorDiagnostics()
    {
        var copy = new AxiosResponse
        {
            Data = Data,
            Status = Status,
            StatusText = StatusText,
            ProtocolVersion = ProtocolVersion,
            BodyBytes = BodyBytes,
            CacheRevalidated = CacheRevalidated,
            Config = Config,
            Request = Request,
            RawBodyBuffer = RawBodyBuffer,
        };

        // 后续尝试会整体换掉头字典，因此这里复制一份而不是共用（值本身是字符串序列，不再变）。
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Headers)
        {
            headers[header.Key] = header.Value;
        }

        copy.Headers = headers;

        // 字节是原始正文的唯一来源：先搬字节（会作废文本），已解码的文本再原样带过去。
        copy.SetRawBody(_rawBody, _rawBodyCharSet, _rawBodyEncoding);
        if (_rawText is { } text)
        {
            copy.RawText = text;
        }

        return copy;
    }

    /// <summary>把非泛型响应复制成 <typeparamref name="TData"/> 的响应；只在响应拦截器换掉响应对象时使用。</summary>
    internal AxiosResponse<TData> AsTyped<TData>()
    {
        var typed = new AxiosResponse<TData>
        {
            Data = DataCast.To<TData>(Data, Config, Request),
            Status = Status,
            StatusText = StatusText,
            ProtocolVersion = ProtocolVersion,
            BodyBytes = BodyBytes,
            RawBodyBuffer = RawBodyBuffer,
            Headers = Headers,
            Config = Config,
            Request = Request
        };

        // 字节是原始正文的唯一来源：先搬字节（会作废文本），已解码的文本再原样带过去。
        typed.SetRawBody(_rawBody, _rawBodyCharSet, _rawBodyEncoding);
        if (_rawText is { } text)
        {
            typed.RawText = text;
        }

        return typed;
    }
}

/// <summary>泛型响应，Data 已按 TData 反序列化。</summary>
public sealed class AxiosResponse<TData> : AxiosResponse
{
    /// <summary>未装箱的原始 Data（<c>base.Data</c> 本身），供管线就地填充。</summary>
    internal object? RawData
    {
        get => base.Data;
        set => base.Data = value;
    }

    /// <summary>已转换成 <typeparamref name="TData"/> 的 Data；转换在请求返回前完成，这里只做拆箱。</summary>
    public new TData? Data
    {
        get => base.Data is null ? default : (TData)base.Data;
        set => base.Data = value;
    }

    /// <summary>取正文；正文为空时抛 <see cref="AxiosException"/>（<c>ERR_BAD_RESPONSE</c>）。</summary>
    /// <remarks>
    /// 「空」既包括 <c>null</c>，也包括长度 0 的字符串：<c>string</c> 目标类型交付的是正文原文，
    /// 204 / 服务端漏写正文得到的就是空串而不是 <c>null</c>，不拦下来等于把断言静默跳过。
    /// </remarks>
    /// <returns>非空的正文。</returns>
    /// <exception cref="AxiosException">正文为空（<c>ERR_BAD_RESPONSE</c>）。</exception>
    public TData DataOrThrow()
    {
        if (base.Data is null or string { Length: 0 })
        {
            throw AxiosException.EmptyBody(Config, Request, this, typeof(TData).Name);
        }

        return (TData)base.Data;
    }
}
