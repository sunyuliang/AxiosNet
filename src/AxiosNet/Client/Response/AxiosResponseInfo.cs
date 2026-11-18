
namespace AxiosNet;

/// <summary>
/// 响应阶段的只读上下文（状态码 / 响应头 / 内容类型），通过 <see cref="AxiosConfig.Response"/> 取用，
/// 供反序列化与 <see cref="AxiosConfig.SerializationSettings.TransformResponse"/> 读取响应头。
/// 只在响应方向有值（请求阶段为 <c>null</c>），且不随配置克隆。
/// </summary>
/// <remarks>
/// <see cref="MediaType"/> / <see cref="CharSet"/> 是**按需解析**的：构造时只留 <c>Content-Type</c> 的原始头值
/// （不复制字符串、不解析），第一次读这两个属性才拆分一次并缓存。没人读它们的场景（流式与字节响应，
/// 或调用方不碰 <see cref="AxiosConfig.Response"/>）就一次都不拆。注意正文转换走的是自己那份按需解析
/// （见 <c>ResponseReader.ConvertData</c>），与本类的惰性状态互不影响。
/// </remarks>
public sealed class AxiosResponseInfo
{
    /// <summary><c>Content-Type</c> 的原始头值（未解析）；没有该头时为 <c>null</c>。</summary>
    private readonly string? _rawContentType;

    /// <summary>解析出的媒体类型主值（首次读 <see cref="MediaType"/> / <see cref="CharSet"/> 时填）。</summary>
    private string? _mediaType;

    /// <summary>解析出的字符集；与媒体类型由同一次拆分得出。</summary>
    private string? _charSet;

    /// <summary>是否已经拆分过 <see cref="_rawContentType"/>（0/1）；并发首次读取可能重复拆分，但结果一致。</summary>
    private int _contentTypeResolved;

    internal AxiosResponseInfo(
        int status,
        string statusText,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string? rawContentType)
    {
        Status = status;
        StatusText = statusText;
        Headers = headers;
        _rawContentType = rawContentType;
    }

    /// <summary>HTTP 状态码（同 <see cref="AxiosResponse.Status"/>，错误状态码下也能读到）。</summary>
    public int Status { get; }

    /// <summary>状态码对应的标准文案（同 <see cref="AxiosResponse.StatusText"/>）。</summary>
    public string StatusText { get; }

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感），与 <see cref="AxiosResponse.Headers"/> 同一实例。</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <summary>响应的媒体类型主值（已去参数），如 <c>application/json</c>；无 Content-Type 时为 <c>null</c>。</summary>
    public string? MediaType
    {
        get
        {
            ResolveContentType();
            return _mediaType;
        }
    }

    /// <summary>响应声明的字符集（<c>charset=</c>），未声明时为 <c>null</c>。</summary>
    public string? CharSet
    {
        get
        {
            ResolveContentType();
            return _charSet;
        }
    }

    /// <summary>取某个响应头的第一个值；不存在返回 <c>null</c>。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>首次读取媒体类型或字符集时拆分原始头值：先写结果再置标记，读到标记的线程一定能看到这两个字段。</summary>
    private void ResolveContentType()
    {
        if (Volatile.Read(ref _contentTypeResolved) != 0 || _rawContentType is null)
        {
            return;
        }

        var (mediaType, charSet) = ContentTypeHeader.Split(_rawContentType);
        _mediaType = mediaType;
        _charSet = charSet;
        Volatile.Write(ref _contentTypeResolved, 1);
    }
}
