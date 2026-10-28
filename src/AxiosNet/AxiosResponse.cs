using AxiosNet.Internal;

namespace AxiosNet;

/// <summary>
/// 非泛型响应，对应 axios response 结构：
/// data / status / statusText / headers / config / request。
/// </summary>
public class AxiosResponse
{
    public object? Data { get; set; }

    public int Status { get; set; }

    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 这次请求实际协商到的 HTTP 版本（<c>1.1</c> / <c>2.0</c> / <c>3.0</c>）；
    /// 默认 2.0 + 自动降级时，只有它能确认这次是否真跑在 h2 上。
    /// </summary>
    public Version? ProtocolVersion { get; set; }

    private IReadOnlyDictionary<string, IEnumerable<string>>? _headers;

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感）。</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers
    {
        get => _headers ??= new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        set => _headers = value;
    }

    public AxiosConfig? Config { get; set; }

    /// <summary>错误响应的正文原文（只有状态码不合法时才有；正常、二进制 / 流式响应为 <c>null</c>）。</summary>
    internal string? RawText { get; set; }

    /// <summary>
    /// 底层 HttpRequestMessage（对应 axios response.request）。
    /// 请求结束后已被释放：<c>Method</c> / <c>RequestUri</c> / <c>Headers</c> 可读，读 <c>Content</c> 会抛 <see cref="ObjectDisposedException"/>。
    /// </summary>
    public HttpRequestMessage? Request { get; set; }

    /// <summary>便捷获取响应头第一个值。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// 便捷获取<b>请求</b>头第一个值（对应 axios 里从 <c>response.request</c> 读请求头）。
    /// 短路响应没有底层请求消息（<see cref="Request"/> 为 <c>null</c>），此时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 请求方向没有合并视图：<c>HttpRequestMessage</c> 的头分成「请求头」与「内容头」两组，
    /// 这里先查请求头、查不到再查内容头（Content-Type / Content-Length 等在内容头里）。
    /// 请求消息被 <c>Dispose</c> 后 <c>Content</c> 引用还在（释放的是它内部持有的流），内容头那时仍读得到；
    /// 但重试过的请求会被显式摘掉正文（<see cref="HttpRequestMessage.Content"/> 置 <c>null</c>，好让同一份正文再发一次），
    /// 内容头那时就读不到了 —— 想每次尝试都读到，请用尝试中间件而不是响应拦截器。
    /// </remarks>
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

        // 内容头：请求消息 Dispose 后 Content 引用仍在，头集合照样可读；重试过的消息 Content 被置 null，这里自然返回 null。
        return request.Content?.Headers.TryGetValues(name, out var contentValues) is true
            ? contentValues.FirstOrDefault()
            : null;
    }

    /// <summary>响应正文字节数（只有非流式响应有值），供遥测写进 span。</summary>
    internal long? BodyBytes { get; set; }

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
}
