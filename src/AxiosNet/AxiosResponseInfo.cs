namespace AxiosNet;

/// <summary>
/// 响应阶段的只读上下文（状态码 / 响应头 / 内容类型），通过 <see cref="AxiosConfig.Response"/> 取用，
/// 供反序列化与 <see cref="AxiosConfig.TransformResponse"/> 读取响应头。
/// 只在响应方向有值（请求阶段为 <c>null</c>），且不随配置克隆。
/// </summary>
public sealed class AxiosResponseInfo
{
    internal AxiosResponseInfo(
        int status,
        string statusText,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string? mediaType,
        string? charSet)
    {
        Status = status;
        StatusText = statusText;
        Headers = headers;
        MediaType = mediaType;
        CharSet = charSet;
    }

    /// <summary>HTTP 状态码（同 <see cref="AxiosResponse.Status"/>，错误状态码下也能读到）。</summary>
    public int Status { get; }

    /// <summary>状态码对应的标准文案（同 <see cref="AxiosResponse.StatusText"/>）。</summary>
    public string StatusText { get; }

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感），与 <see cref="AxiosResponse.Headers"/> 同一实例。</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <summary>响应的媒体类型主值（已去参数），如 <c>application/json</c>；无 Content-Type 时为 <c>null</c>。</summary>
    public string? MediaType { get; }

    /// <summary>响应声明的字符集（<c>charset=</c>），未声明时为 <c>null</c>。</summary>
    public string? CharSet { get; }

    /// <summary>取某个响应头的第一个值；不存在返回 <c>null</c>。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}
