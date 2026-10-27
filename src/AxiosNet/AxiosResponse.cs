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
