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

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感）。</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; set; }
        = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

    public AxiosConfig? Config { get; set; }

    /// <summary>
    /// 底层 HttpRequestMessage（对应 axios response.request）。
    /// <para>
    /// 请求结束后它已被释放：<c>Method</c> / <c>RequestUri</c> / <c>Headers</c> 等内存态属性可读，
    /// <c>Content</c> 的读取操作会抛 <see cref="ObjectDisposedException"/>。
    /// </para>
    /// </summary>
    public HttpRequestMessage? Request { get; set; }

    /// <summary>便捷获取响应头第一个值。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    internal AxiosResponse<TData> AsTyped<TData>()
    {
        return new AxiosResponse<TData>
        {
            Data = DataCast.To<TData>(Data, Config, Request),
            Status = Status,
            StatusText = StatusText,
            Headers = Headers,
            Config = Config,
            Request = Request
        };
    }
}

/// <summary>
/// 泛型响应，Data 已按 TData 反序列化。
/// </summary>
public sealed class AxiosResponse<TData> : AxiosResponse
{
    /// <summary>
    /// 已转换成 <typeparamref name="TData"/> 的 Data。
    /// <para>
    /// 转换在泛型请求返回时（<see cref="AxiosResponse.AsTyped{TData}"/>）就完成了，
    /// 因此这里只做拆箱，不再每次读取都跑一遍转换，也不把类型不匹配的检查推迟到属性 getter
    /// （getter 抛业务异常会让日志、调试器、序列化统统在这里炸掉）。
    /// </para>
    /// </summary>
    public new TData? Data
    {
        get => base.Data is null ? default : (TData)base.Data;
        set => base.Data = value;
    }
}
