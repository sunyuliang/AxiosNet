using System.Text;

namespace AxiosNet;

/// <summary>
/// 短路响应：本次请求<b>不发出去</b>，直接把这份结果当作响应返回。
/// 挂在 <see cref="AxiosConfig.ShortCircuit"/> 上，供响应缓存、mock、离线回放、契约测试使用。
/// 闸门（排队 / 并发名额）、编码、连接租约、发送、重试、熔断、遥测 span 都不参与；响应方向的一切照常：
/// 按本次调用的 <see cref="AxiosConfig.ResponseType"/> / 泛型参数反序列化、跑 transformResponse、
/// 过 <see cref="AxiosConfig.CallbackSettings.ValidateStatus"/>、执行响应拦截器，范围校验也照常。
/// 唯一可辨的痕迹是 <see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// </summary>
/// <remarks>
/// <see cref="Body"/> 是原始字节，按每次调用的类型重新解析；<see cref="Data"/> 是已成型的结果，原样交付。
/// 本对象只描述「这一次请求」：<see cref="AxiosConfig.Clone"/> 会清空它，放在 Defaults 上不生效。
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

            // GetBytes 不写 BOM，命中时不会被解码层当成多余前缀。
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    /// <summary>正文已是一个对象：原样作为 Data（不序列化，适合 mock 与固定夹具）。</summary>
    public static AxiosShortCircuit From(object? data, int status = 200)
        => new() { Status = status, Data = data };
}
