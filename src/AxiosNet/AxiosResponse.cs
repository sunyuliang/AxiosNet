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

    /// <summary>底层 HttpRequestMessage（对应 axios response.request）。</summary>
    public HttpRequestMessage? Request { get; set; }

    /// <summary>便捷获取响应头第一个值。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    internal AxiosResponse<TData> AsTyped<TData>()
    {
        return new AxiosResponse<TData>
        {
            Data = Data is null ? default : (TData?)Data,
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
    public new TData? Data
    {
        get => base.Data is null ? default : (TData)base.Data;
        set => base.Data = value;
    }
}
