
namespace AxiosNet;

/// <summary>
/// 非泛型响应，对应 axios response 结构：
/// data / status / statusText / headers / config / request。
/// </summary>
public class AxiosResponse
{
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

    /// <summary>失败响应的正文原文（字符串形态），正常响应为 <c>null</c>。</summary>
    /// <remarks>
    /// 状态码被 <see cref="AxiosConfig.ValidateStatus"/> 拒绝或响应装配失败时保留；二进制与流式成功响应为 <c>null</c>。
    /// 任何响应（含 2xx）的原始字节请用 <see cref="AxiosConfig.ObservabilitySettings.OnRawResponse"/> 或
    /// <see cref="AxiosConfig.ObservabilitySettings.CaptureRawResponseBody"/>。
    /// </remarks>
    public string? RawText { get; internal set; }

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
    internal long? BodyBytes { get; set; }

    /// <summary>这次响应是条件请求的 304 合并出来的（正文来自缓存），供遥测与日志区分「未命中」。</summary>
    internal bool CacheRevalidated { get; set; }

    /// <summary>响应正文的原始字节（只读），仅在打开 <see cref="AxiosConfig.ObservabilitySettings.CaptureRawResponseBody"/> 时有值。</summary>
    /// <remarks>
    /// 网络流式响应（<see cref="ResponseType.Stream"/>）没有完整字节；空正文是长度 0 而非 <c>null</c>。
    /// 与 <see cref="AxiosConfig.ObservabilitySettings.OnRawResponse"/> 收到的一致（已解压、未解码）。
    /// </remarks>
    public ReadOnlyMemory<byte>? RawBody { get; internal set; }

    /// <summary>缓存落库用的正文缓冲：与 <see cref="RawBody"/> 指向同一块数组，落库或失败收尾后清空。</summary>
    internal byte[]? RawBodyBuffer { get; set; }

    /// <summary>把非泛型响应复制成 <typeparamref name="TData"/> 的响应；只在响应拦截器换掉响应对象时使用。</summary>
    internal AxiosResponse<TData> AsTyped<TData>()
    {
        return new AxiosResponse<TData>
        {
            Data = DataCast.To<TData>(Data, Config, Request),
            RawText = RawText,
            Status = Status,
            StatusText = StatusText,
            ProtocolVersion = ProtocolVersion,
            BodyBytes = BodyBytes,
            RawBody = RawBody,
            RawBodyBuffer = RawBodyBuffer,
            Headers = Headers,
            Config = Config,
            Request = Request
        };
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
    /// <returns>非空的正文。</returns>
    /// <exception cref="AxiosException">正文为空（<c>ERR_BAD_RESPONSE</c>）。</exception>
    public TData DataOrThrow()
        => base.Data is null
            ? throw AxiosException.EmptyBody(Config, Request, this, typeof(TData).Name)
            : (TData)base.Data;
}
