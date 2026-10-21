namespace AxiosNet;

/// <summary>
/// 响应阶段的只读上下文（状态码 / 响应头 / 内容类型），通过 <see cref="AxiosConfig.Response"/> 取用。
/// <para>
/// 存在的意义：反序列化和 <see cref="AxiosConfig.TransformResponse"/> 都只能看到
/// <see cref="AxiosConfig"/>，看不到响应头 —— 于是「按 <c>X-Schema-Version</c> 选择解析规则」
/// 「把 <c>X-Total-Count</c> 贴到分页对象上」这类需求没有落点。本类型补上这个缺口。
/// </para>
/// <para>
/// 三个使用约束：
/// <list type="bullet">
/// <item><b>只在响应方向有值</b>：请求阶段（<see cref="Serialization.IContentSerializer.Serialize"/>）
/// 读到的是 <c>null</c>；</item>
/// <item><b>零拷贝</b>：<see cref="Headers"/> 与 <see cref="AxiosResponse.Headers"/> 是同一个字典实例，
/// 不会为了它多复制一份响应头；</item>
/// <item><b>不随配置克隆</b>：<see cref="AxiosConfig.Clone"/> 会把它清空，避免上一次响应的头
/// 被带进下一次请求的序列化器。</item>
/// </list>
/// </para>
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

    /// <summary>HTTP 状态码（同 <see cref="AxiosResponse.Status"/>，在错误状态码下也能读到）。</summary>
    public int Status { get; }

    /// <summary>状态码对应的标准文案（同 <see cref="AxiosResponse.StatusText"/>）。</summary>
    public string StatusText { get; }

    /// <summary>
    /// 合并了响应头与内容头的只读字典（大小写不敏感），与 <see cref="AxiosResponse.Headers"/> 同一实例。
    /// <para>值为该头的全部原始值（同名头可能是多个）。</para>
    /// </summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <summary>响应的媒体类型主值（已去参数），如 <c>application/json</c>；无 Content-Type 时为 <c>null</c>。</summary>
    public string? MediaType { get; }

    /// <summary>响应声明的字符集（<c>charset=</c>），未声明时为 <c>null</c>。</summary>
    public string? CharSet { get; }

    /// <summary>取某个响应头的第一个值；不存在返回 <c>null</c>。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}
