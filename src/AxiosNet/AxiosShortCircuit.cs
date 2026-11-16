using System.Collections.ObjectModel;
using System.Text;

namespace AxiosNet;

/// <summary>
/// 短路响应：本次请求<b>不发出去</b>，直接把这份结果当作响应返回；挂在 <see cref="AxiosConfig.ShortCircuit"/> 上。
/// </summary>
/// <remarks>
/// 闸门（排队 / 并发槽位）、编码、连接租约、发送、重试、熔断、遥测 span 都不参与；响应方向照常处理——
/// 按本次 <c>dataType</c> / responseType 重新解析、跑 transformResponse、过 <see cref="AxiosConfig.ValidateStatus"/>、
/// 执行响应拦截器。唯一可辨的痕迹是 <see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// <para><see cref="Body"/> 是原始字节，按每次调用的类型重新解析；<see cref="Data"/> 是已成型的结果，原样交付。</para>
/// <para>本对象只描述「这一次请求」：<see cref="AxiosConfig.Clone"/> 会清空它，放在 Defaults 上不生效。</para>
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
    /// 响应头的<b>全部值</b>（每个头的每一条）：给自定义 <see cref="Cache.IAxiosCacheStore"/>
    /// <b>保存</b>条目用的只读出口。
    /// </summary>
    /// <remarks>
    /// 数据来自内部的多值字段（那个成员只对包内可见）；本属性只有 get —— 写入请走 <see cref="Restore"/>。
    /// 没有多值头时（自己 <c>new</c> 的条目、只填了 <see cref="Headers"/> 的条目）为 <c>null</c>。
    /// <para>
    /// 存储只保存 <see cref="Headers"/>（单值）会丢这三样：多条 <c>Set-Cookie</c> / <c>Vary</c> 的全部值、
    /// <c>ETag</c> / <c>Last-Modified</c> 的<b>优先</b>来源（见 <c>Cache/CacheValidators.cs</c>）、
    /// 以及同名多值头（如多个 <c>WWW-Authenticate</c>）除首值以外的部分。
    /// </para>
    /// <para>
    /// 返回的是<b>独立快照</b>（深拷贝 + 只读包装，键按大小写不敏感）：调用方对它做任何修改都不会影响条目本身，
    /// 也不会污染同一条目后续命中 / 落库的结果。每次读取都新建一份，适合「落库那一刻读一次」的用法，
    /// 不要放进循环里反复取。
    /// </para>
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
    /// 从自己的存储里<b>重建</b>一条缓存条目：自定义 <see cref="Cache.IAxiosCacheStore"/>（Redis / 共享缓存 /
    /// 自己序列化的文件）取回记录后，用本工厂把各字段一次性装回一个条目，再交给库当作命中结果。
    /// </summary>
    /// <remarks>
    /// 为什么需要这个入口：<see cref="AxiosShortCircuit"/> 有三个只对包内可见的成员，
    /// 包外<b>写</b>不进去（读可以走 <see cref="MultiValueHeaderValues"/> / <see cref="CachedAt"/> / <see cref="CachedAge"/>），
    /// 于是「照自己的格式存、再照自己的格式读回」就会静默降级（不报错、只是行为不对）：
    /// <list type="bullet">
    /// <item><see cref="MultiValueHeaders"/>：响应头的<b>全部值</b>。多条 <c>Set-Cookie</c> / <c>Vary</c> 明细
    /// 单值字典装不下；不给它就只能按首值折叠（或整条丢失），条件请求与 304 合并读到的头因此变样。</item>
    /// <item><see cref="ReceivedAt"/> + <see cref="StoredAge"/>：落库时刻与落库那一刻的龄期（RFC 9111 的
    /// <c>current_age</c>）。两者缺一，命中时的 <c>Age</c> 头就停在写入时刻、不再随在缓存里停留的时间推进。</item>
    /// <item><see cref="MultiValueHeaders"/> 还是 <c>ETag</c> / <c>Last-Modified</c> 的<b>优先</b>来源
    /// （见 <c>Cache/CacheValidators.cs</c>）：只填 <see cref="Headers"/> 时条件请求仍可用，但多值条目上的
    /// 校验器会被降级成「只有首值」。</item>
    /// </list>
    /// <para>参数与同名属性一一对应，默认值即「这条记录里没有这一项」。<paramref name="body"/> 与
    /// <see cref="Data"/> 互斥——本工厂只接受原始字节（存储后端存的就是字节）。</para>
    /// <para>
    /// <b>注意交付规则</b>：<paramref name="multiValueHeaders"/> 非空时，库只用它建响应头，
    /// <paramref name="headers"/> 会被整份忽略（见 <c>Internal/ResponseReader.cs</c> 的短路头装配）。
    /// 因此存储要实现「命中的响应头与原响应一致」，就得像库自己那样两边同源填写：
    /// 单值字典 = 每个头的首值，多值字典 = 每个头的全部值。只填单值字典同样可用，只是丢掉同名多值。
    /// </para>
    /// <para>
    /// <b>传入的字典按引用收下，本工厂不做复制</b>（复制会落在每次命中的热路径上）：交出所有权后不要再改它们，
    /// 也不要跨条目复用同一个可变字典。存储实现自然形态是「反序列化出一份新的字典 → 交给本工厂」，那样天然安全。
    /// 唯一例外：<paramref name="multiValueHeaders"/> 里出现「值整份为 <c>null</c>」的条目时（反序列化常见形态，
    /// 如 <c>{"Vary":null}</c>），本工厂会复制一份并剔掉这些条目 —— 否则下游按「每个值非 null」读的地方
    /// （条件请求校验器、导出视图）会直接抛 <see cref="NullReferenceException"/> / <see cref="ArgumentNullException"/>。
    /// </para>
    /// </remarks>
    /// <param name="body">响应原始字节；存储里没有正文时传 <c>null</c>。</param>
    /// <param name="headers">单值头字典（大小写不敏感地查）；没有传 <c>null</c>。</param>
    /// <param name="multiValueHeaders">多值头（每个头的全部值）；命中时优先于 <paramref name="headers"/>。值整份为 <c>null</c> 的条目视为「没有这个头」。</param>
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
    /// 为什么必须在入口做：<c>Restore</c> 是包外写入这三个字段的唯一通道，而调用方最常见形态是
    /// 「反序列化出一份字典交给它」（各存储后端的自然形态）——<c>System.Text.Json</c> 遇到
    /// <c>{"Vary":null}</c> 就会产出 <c>null</c> 值。下游有<b>两处</b>按 <c>foreach (header.Value)</c>
    /// 直接读、自己不判空：<see cref="Cache.CacheValidators"/>（条件请求必经，取到 <c>null</c> 值会
    /// <see cref="NullReferenceException"/>）与本类的 <see cref="MultiValueHeaderValues"/> 导出视图
    /// （<c>new List&lt;string&gt;(null)</c> 会 <see cref="ArgumentNullException"/>）。
    /// 另三处读取点（交付装配、304 合并、落库）各自已经跳过 <c>null</c> 值，不受影响 ——
    /// 在入口归一只是把剩下这两处一起挡住，且这里只付一次遍历。
    /// <para>
    /// 分配：没有 null 值时原样返回传入的字典（<b>不复制</b>，常态零分配，与此前「按引用收下」一致）；
    /// 只有确实存在 null 值时才复制一份干净的。
    /// </para>
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
