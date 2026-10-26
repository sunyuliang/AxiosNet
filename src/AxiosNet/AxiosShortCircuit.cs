using System.Text;

namespace AxiosNet;

/// <summary>
/// 短路响应：本次请求<b>不发出去</b>，直接把这份结果当作响应返回。
/// <para>
/// 用途是拦截器只能改配置、没有「直接给响应」的出口（请求拦截器的返回值是 <see cref="AxiosConfig"/>，
/// 管线之后一定会进入编码与发送）。把本对象挂到 <see cref="AxiosConfig.ShortCircuit"/> 上，就是那个出口：
/// 响应缓存、mock、离线回放、契约测试都在调用方一侧实现，库只负责「按真实响应的语义交付它」。
/// </para>
/// <para>
/// <b>命中语义</b>：闸门（排队 / 并发名额）、编码、连接租约、发送、重试、熔断、遥测 span 全部不参与
/// —— 这些描述的都是「一次 HTTP 操作」，而这次没有发生。反过来，<b>响应方向</b>的一切照常：
/// 正文按本次调用的 <see cref="AxiosConfig.ResponseType"/> / 泛型参数重新反序列化、transformResponse 照常执行、
/// 状态码照常过 <see cref="AxiosConfig.CallbackSettings.ValidateStatus"/>、响应拦截器照常执行。
/// 范围校验（<see cref="AxiosConfig.Timeout"/> / 各项上限要为正）也照常：它判的是「这份配置是否可执行」，
/// 与有没有发出请求无关 —— 同一个错误不该因为命中缓存就静默通过。
/// 因此调用方看到的响应与真实响应没有区别（唯一可辨的痕迹是 <see cref="AxiosResponse.Request"/> 为 <c>null</c>）。
/// </para>
/// <para>
/// <b>两种载荷二选一</b>：<see cref="Body"/> 是原始字节，按<b>每次调用</b>的类型重新解析（缓存必须用它 ——
/// 同一份字节喂给 <c>GetAsync&lt;User&gt;</c> 与 <c>GetAsync&lt;string&gt;</c> 应当得到不同形态）；
/// <see cref="Data"/> 是已成型的结果，原样交付、不解析也不跑 transformResponse（mock 用它最省事）。
/// </para>
/// <para>
/// <b>不是实例级设置</b>：本对象只描述「这一次请求」。<see cref="AxiosConfig.Clone"/> 会清空它，
/// 因此把它放在 <see cref="AxiosClient.Defaults"/> 上不生效，回放 <see cref="AxiosResponse.Config"/>
/// 也不会重放上一次的短路结果。
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 1) 在请求拦截器里查缓存：命中就不发请求
/// client.Interceptors.Request.Use(config =&gt;
/// {
///     if (cache.TryGet(config, out var entry))
///     {
///         config.ShortCircuit = new AxiosShortCircuit
///         {
///             Status = entry.Status,
///             ContentType = entry.ContentType,
///             Body = entry.Body          // 字节：按本次调用的类型重新反序列化
///         };
///     }
///
///     return config;
/// });
///
/// // 2) 按请求直接给：mock / 契约测试
/// var response = await client.GetAsync&lt;User&gt;("https://api.example.com/users/1", c =&gt;
///     c.ShortCircuit = AxiosShortCircuit.From(new User { Id = 1, Name = "stub" }));
/// </code>
/// </example>
public sealed class AxiosShortCircuit
{
    /// <summary>响应状态码，默认 200。非 2xx 同样走 validateStatus，不合法时按统一错误模型抛出。</summary>
    public int Status { get; set; } = 200;

    /// <summary>状态码文案，默认按状态码取标准文案（同真实响应）。</summary>
    public string? StatusText { get; set; }

    /// <summary>
    /// 响应正文的媒体类型，等价于在 <see cref="Headers"/> 里写 <c>Content-Type</c>（两者都写时以本项为准）。
    /// <para>
    /// 它决定正文按文本还是二进制处理，带 <c>charset=</c> 时也决定解码 —— 缓存真实响应时请把原始
    /// 头的值一起带回来，丢了字符集会让 GBK 之类的正文在命中时变成乱码。
    /// </para>
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>其余响应头（大小写不敏感，单值）：调用方的 transformResponse 与 <c>response.GetHeader</c> 都能读到。</summary>
    public IDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// 响应正文字节（原始形态，未解码）。与 <see cref="Data"/> 互斥。
    /// <para>
    /// 交付时按本次调用的 responseType 与泛型参数重新处理：JSON → 强类型、<c>text</c> → 字符串、
    /// <c>arraybuffer</c> → 字节数组、<c>stream</c> → 一份独立的内存流（每次都新建，不与缓存共享流对象）。
    /// </para>
    /// </summary>
    public byte[]? Body { get; set; }

    /// <summary>
    /// 已经成型的结果：原样作为 <see cref="AxiosResponse.Data"/> 交付，<b>不解析、不跑 transformResponse</b>。
    /// 与 <see cref="Body"/> 互斥；泛型入口会校验它与目标类型兼容（不兼容报 <c>ERR_BAD_RESPONSE</c>）。
    /// <para>所有权归调用方：库不释放它（哪怕响应拦截器换掉了响应对象）。</para>
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// 协商到的 HTTP 版本（<c>1.1</c> / <c>2.0</c> / <c>3.0</c>）。
    /// <para>默认 <c>null</c>：这次没有发生过协议协商，不替调用方编一个值。</para>
    /// </summary>
    public Version? ProtocolVersion { get; set; }

    /// <summary>正文是一段 JSON 文本：以 UTF-8 字节交付，<c>Content-Type: application/json</c>。</summary>
    /// <param name="json">JSON 文本（原样放进正文，不做语法校验）。</param>
    /// <param name="status">状态码，默认 200。</param>
    public static AxiosShortCircuit Json(string json, int status = 200)
    {
        ArgumentNullException.ThrowIfNull(json);

        return new AxiosShortCircuit
        {
            Status = status,
            ContentType = AxiosContentType.Json,

            // Encoding.UTF8.GetBytes 不写 BOM（GetPreamble 才有）：命中时不会被解码层当成多余前缀。
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    /// <summary>正文已是一个对象：原样作为 Data（不序列化，容量为零，适合 mock 与固定夹具）。</summary>
    /// <param name="data">要交付的数据；<c>null</c> 表示空正文。</param>
    /// <param name="status">状态码，默认 200。</param>
    public static AxiosShortCircuit From(object? data, int status = 200)
        => new() { Status = status, Data = data };
}
