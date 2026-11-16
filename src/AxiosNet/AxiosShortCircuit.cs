using System.Collections.ObjectModel;
using System.Text;

namespace AxiosNet;

/// <summary>
/// 短路响应：本次请求不发出去，直接把这份结果当作响应返回；挂在 <see cref="AxiosConfig.ShortCircuit"/> 上。
/// </summary>
/// <remarks>
/// 闸门、编码、连接租约、发送、重试、熔断与遥测 span 都不参与；响应方向照常处理——按本次 responseType 重新解析、
/// 跑 transformResponse、过 <see cref="AxiosConfig.ValidateStatus"/>、执行响应拦截器。<see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// <see cref="Body"/> 是原始字节（按每次调用的类型重新解析），<see cref="Data"/> 是已成型的结果（原样交付）。
/// 只描述这一次请求：<see cref="AxiosConfig.Clone"/> 会清空它，放在 Defaults 上不生效。
/// </remarks>
/// <example>
/// <code>
/// // 请求拦截器里查缓存：命中就不发请求
/// client.Interceptors.Request.Use(config =&gt;
/// {
///     if (cache.TryGet(config, out var entry))
///     {
///         config.ShortCircuit = new AxiosShortCircuit { Status = entry.Status, Body = entry.Body };
///     }
///
///     return config;
/// });
/// </code>
/// </example>
public sealed class AxiosShortCircuit
{
    /// <summary>响应状态码，默认 200。非 2xx 同样走 validateStatus，不合法时按统一错误模型抛出。</summary>
    public int Status { get; set; } = 200;

    /// <summary>状态码文案，默认按状态码取标准文案（同真实响应）。</summary>
    public string? StatusText { get; set; }

    /// <summary>响应正文的媒体类型，等价于 <see cref="Headers"/> 里的 <c>Content-Type</c>（两者都写时以本项为准）；带 <c>charset=</c> 时决定解码。</summary>
    public string? ContentType { get; set; }

    /// <summary>其余响应头（大小写不敏感，单值）：调用方的 transformResponse 与 <c>response.GetHeader</c> 都能读到。</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// 内部：多值响应头（304 合并用，优先于 <see cref="Headers"/>）。<c>Set-Cookie</c> 不能按逗号折叠，
    /// 单值字典装不下，走这里原样交给响应。
    /// </summary>
    internal IReadOnlyDictionary<string, IEnumerable<string>>? MultiValueHeaders { get; set; }

    /// <summary>
    /// 内部：缓存条目的落库时刻。命中时用它与 <see cref="StoredAge"/> 把 <c>Age</c> 头推进到「当前龄期」。
    /// </summary>
    internal DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>内部：落库那一刻的龄期（RFC 9111 的 current_age），命中时叠加在缓存里停留的时间。</summary>
    internal TimeSpan? StoredAge { get; set; }

    /// <summary>
    /// 响应头的全部值（每个头的每一条）：给 <see cref="Cache.IAxiosCacheStore"/> 保存条目用的只读出口。
    /// </summary>
    /// <remarks>
    /// 只读，写入请走 <see cref="Restore"/>；没有多值头时为 <c>null</c>。
    /// 只保存 <see cref="Headers"/>（单值）会丢掉多条 <c>Set-Cookie</c> / <c>Vary</c> 的全部值、<c>ETag</c> / <c>Last-Modified</c>
    /// 的优先来源，以及同名多值头除首值以外的部分。
    /// 返回独立快照（深拷贝 + 只读包装，键大小写不敏感），每次读取都新建一份，适合落库时读一次、不要放进循环。
    /// </remarks>
    public IReadOnlyDictionary<string, IEnumerable<string>>? MultiValueHeaderValues
    {
        get
        {
            var source = MultiValueHeaders;
            if (source is null)
            {
                return null;
            }

            // 深拷贝：内部存的是 Dictionary<string, List<string>>，只包一层 ReadOnlyDictionary 的话，
            // 调用方仍可强转回 Dictionary / List 改到库内状态；值因此也一并换成 ReadOnlyCollection。
            // 分配只发生在本属性被读取时（自定义存储落库那一刻），不在任何库内热路径上。
            var snapshot = new Dictionary<string, IEnumerable<string>>(source.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var header in source)
            {
                snapshot[header.Key] = new List<string>(header.Value).AsReadOnly();
            }

            return new ReadOnlyDictionary<string, IEnumerable<string>>(snapshot);
        }
    }

    /// <summary>
    /// 这条缓存的<b>落库时刻</b>：给自定义 <see cref="Cache.IAxiosCacheStore"/> 保存条目用的只读出口，
    /// 与 <see cref="CachedAge"/> 一起决定命中时的 <c>Age</c>。
    /// </summary>
    /// <remarks>
    /// 只有 get，写入请走 <see cref="Restore"/>；缺失时为 <c>null</c>，此时命中响应的 <c>Age</c> 会停在
    /// 存进去的那个数值、不再随在缓存里停留的时间推进。
    /// </remarks>
    public DateTimeOffset? CachedAt => ReceivedAt;

    /// <summary>
    /// 这条缓存<b>落库那一刻的龄期</b>（RFC 9111 的 <c>current_age</c>）：给自定义
    /// <see cref="Cache.IAxiosCacheStore"/> 保存条目用的只读出口。
    /// </summary>
    /// <remarks>
    /// 命中时的 <c>Age</c> = 本值 + 在缓存里停留的时间（<c>now - <see cref="CachedAt"/></c>）。
    /// <b>前提是条目头里本来就有 <c>Age</c></b>：原响应没带这个头时库不会凭空造一个，此时本值只作记录、不影响交付。
    /// 只有 get，写入请走 <see cref="Restore"/>；缺失时为 <c>null</c>。
    /// </remarks>
    public TimeSpan? CachedAge => StoredAge;

    /// <summary>响应正文字节（原始未解码），与 <see cref="Data"/> 互斥；交付时按本次调用的 responseType 与泛型参数重新处理。</summary>
    public byte[]? Body { get; set; }

    /// <summary>已成型的结果：原样作为 <see cref="AxiosResponse.Data"/> 交付，不解析、不跑 transformResponse；与 <see cref="Body"/> 互斥，所有权归调用方。</summary>
    public object? Data { get; set; }

    /// <summary>协商到的 HTTP 版本（<c>1.1</c> / <c>2.0</c> / <c>3.0</c>），默认 <c>null</c>。</summary>
    public Version? ProtocolVersion { get; set; }

    /// <summary>正文是一段 JSON 文本：以 UTF-8 字节交付，<c>Content-Type: application/json</c>，不做语法校验。</summary>
    public static AxiosShortCircuit Json(string json, int status = 200)
    {
        ArgumentNullException.ThrowIfNull(json);

        return new AxiosShortCircuit
        {
            Status = status,
            ContentType = AxiosContentType.Json,

            // 不写 BOM。
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    /// <summary>正文已是一个对象：原样作为 Data（不序列化，适合 mock 与固定夹具）。</summary>
    public static AxiosShortCircuit From(object? data, int status = 200)
        => new() { Status = status, Data = data };

    /// <summary>
    /// 从存储记录重建一条缓存条目：自定义 <see cref="Cache.IAxiosCacheStore"/> 取回记录后交给库当作命中结果。
    /// </summary>
    /// <remarks>
    /// 参数与同名属性一一对应，默认值即「记录里没有这一项」；<paramref name="body"/> 与 <see cref="Data"/> 互斥。
    /// <paramref name="multiValueHeaders"/> 非空时库只用它建响应头，<paramref name="headers"/> 被整份忽略；
    /// 值整份为 <c>null</c> 的条目视为「没有这个头」。
    /// 字典按引用收下、不复制：交出所有权后不要再改，也不要跨条目复用同一个可变字典。
    /// </remarks>
    /// <param name="body">响应原始字节；没有正文时传 <c>null</c>。</param>
    /// <param name="headers">单值头字典；没有传 <c>null</c>。</param>
    /// <param name="multiValueHeaders">多值头（每个头的全部值）；命中时优先于 <paramref name="headers"/>。</param>
    /// <param name="receivedAt">落库时刻；与 <paramref name="storedAge"/> 一起决定命中时的 <c>Age</c>。</param>
    /// <param name="storedAge">落库那一刻的龄期（<c>current_age</c>）。</param>
    /// <param name="contentType">正文媒体类型，等价于头里的 <c>Content-Type</c>（两者都给时以本项为准）。</param>
    /// <param name="status">状态码，默认 200。</param>
    /// <param name="statusText">状态码文案；<c>null</c> 时按状态码取标准文案。</param>
    /// <param name="protocolVersion">协商到的 HTTP 版本；<c>null</c> 表示未知。</param>
    /// <returns>可直接赋给 <see cref="AxiosConfig.ShortCircuit"/> 的条目。</returns>
    public static AxiosShortCircuit Restore(
        byte[]? body,
        IDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, IEnumerable<string>>? multiValueHeaders = null,
        DateTimeOffset? receivedAt = null,
        TimeSpan? storedAge = null,
        string? contentType = null,
        int status = 200,
        string? statusText = null,
        Version? protocolVersion = null)
        => new()
        {
            Body = body,
            Headers = headers,
            ContentType = contentType,
            Status = status,
            StatusText = statusText,
            ProtocolVersion = protocolVersion,

            // 三项是包内字段：只有类内（这里）能赋值，包外只能经由本工厂填进来。
            // 多值字典先归一：值为 null 的条目在这里剔掉，下游（交付装配 / 校验器 / 304 合并）都按
            // 「每个值非 null」读，不必逐点判空。
            MultiValueHeaders = NormalizeMultiValueHeaders(multiValueHeaders),
            ReceivedAt = receivedAt,
            StoredAge = storedAge
        };

    /// <summary>
    /// 归一多值头：剔掉「值整份为 <c>null</c>」的条目（等同「没有这个头」）。
    /// </summary>
    /// <remarks>
    /// 下游有按 <c>foreach (header.Value)</c> 不判空的读取点（条件请求校验器、导出视图），<c>null</c> 值会直接抛异常，故在入口归一。
    /// 没有 <c>null</c> 值时原样返回传入的字典（不复制），确实存在时才复制一份干净的。
    /// </remarks>
    /// <param name="multiValueHeaders">调用方给的字典，可能为 <c>null</c>。</param>
    /// <returns>值全部非 null 的字典（可能是同一个实例）。</returns>
    private static IReadOnlyDictionary<string, IEnumerable<string>>? NormalizeMultiValueHeaders(
        IReadOnlyDictionary<string, IEnumerable<string>>? multiValueHeaders)
    {
        if (multiValueHeaders is null || multiValueHeaders.Count == 0)
        {
            return multiValueHeaders;
        }

        var hasNullValue = false;
        foreach (var header in multiValueHeaders)
        {
            if (header.Value is null)
            {
                hasNullValue = true;
                break;
            }
        }

        if (!hasNullValue)
        {
            return multiValueHeaders;
        }

        var cleaned = new Dictionary<string, IEnumerable<string>>(
            multiValueHeaders.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var header in multiValueHeaders)
        {
            if (header.Value is not null)
            {
                cleaned[header.Key] = header.Value;
            }
        }

        return cleaned;
    }
}
