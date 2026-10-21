using System.Text;

namespace AxiosNet.Serialization;

/// <summary>
/// 请求体 / 响应体的<b>格式编解码</b>抽象：JSON（默认）、XML、YAML、Protobuf、MessagePack 等都通过它接入。
/// <para>边界只有一条：<b>业务对象 ↔ 该格式的内容</b>。库负责分派，不解释格式：</para>
/// <list type="bullet">
/// <item>请求：string / byte[] / Stream / HttpContent 原样发送（只套媒体类型）；表单与 multipart 是独立通道，
/// 不经过本接口；基础类型由框架直接取文本；其余（对象、字典、dynamic、集合）交给 <see cref="Serialize"/>。</item>
/// <item>响应：byte[] / Stream / string / 基础类型由框架直接转换；只有<b>业务对象</b>才调用 <c>Deserialize</c>。</item>
/// <item>响应形态由响应 <c>Content-Type</c> 判定：文本走 string 入口，非文本走字节入口且不做解码。</item>
/// </list>
/// <para>
/// 实现会被并发请求共享，必须无状态或线程安全；抛出的异常由库统一包装成
/// <c>ERR_BAD_RESPONSE</c> / <c>ERR_BAD_REQUEST</c>，不需要自己构造 <see cref="AxiosException"/>。
/// </para>
/// </summary>
/// <example>
/// <code>
/// public sealed class YamlSerializer : IContentSerializer
/// {
///     public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);
///     public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
/// }
///
/// AxiosContent.Serializer = new YamlSerializer();                  // 全局显式注入（一个应用一种格式）
/// AxiosContent.Register("application/yaml", new YamlSerializer()); // 或注册：未注入时按 Content-Type 自动匹配
/// </code>
/// </example>
public interface IContentSerializer
{
    /// <summary>
    /// 本格式对应的媒体类型（<c>Content-Type</c> 主值，如 <c>application/json</c>、<c>application/xml</c>）。
    /// <para>
    /// 用途只有一个：<b>请求方向</b>调用方没显式设置 <see cref="AxiosConfig.ContentType"/> 时，库把它写进请求头 ——
    /// 与 axios 的 <c>headers.setContentType(..., false)</c> 同构。因此注入 XML / protobuf 序列化器
    /// 就等于整条链路换格式，不必手动配对头与正文。
    /// </para>
    /// <para>
    /// 默认 <c>null</c> = <b>不自报</b>，沿用 <see cref="AxiosConfig.ContentType"/>；调用方显式设置过时永远以调用方为准。
    /// 响应方向不看本值：解析用哪个序列化器由<b>响应的</b> <c>Content-Type</c> 决定。
    /// </para>
    /// </summary>
    string? MediaType => null;

    /// <summary>
    /// 把业务对象序列化为该格式的文本。库按 <see cref="AxiosConfig.RequestEncoding"/> 编码成字节并补 charset；
    /// 二进制格式请由调用方自行序列化成 <c>byte[]</c> 作为 <see cref="AxiosConfig.Data"/> 传入。
    /// <para>
    /// 只会收到「对象 / 字典 / dynamic / 集合」；<b>基础类型不会进来</b> —— 框架直接取其文本发送
    /// （裸标量没有格式可言，XML / YAML 也无法表达无名根标量）。
    /// </para>
    /// <para>
    /// <b>可以写请求头</b>：<paramref name="config"/> 是本次请求独占的副本，写的头在正文构造完成后统一应用，
    /// 因此能覆盖 Content-Type 的默认值，且不污染实例默认值或并发请求（multipart 的 Content-Type 除外）。
    /// </para>
    /// <para>
    /// ⚠️ 反过来，给 <see cref="AxiosConfig.ContentType"/> 赋值<b>对本次请求无效</b>（正文已按旧值构造完毕）。
    /// 声明格式请用 <see cref="MediaType"/>，或写 <c>config.Headers.Set("Content-Type", ...)</c>；
    /// 与格式无关的头（追踪 ID、租户）用 <see cref="AxiosConfig.TransformRequest"/>。
    /// </para>
    /// </summary>
    string Serialize(object? value, Type type, AxiosConfig config);

    /// <summary>
    /// 把该格式的文本反序列化为业务对象，返回值原样进入 <c>Response.Data</c>。
    /// <para>
    /// 只有「目标类型是业务对象」且响应为<b>文本</b>（含无 Content-Type）时才会被调用；基础类型由库直接解析。
    /// 此时请求头早已发出，改 <see cref="AxiosConfig.Headers"/> 没有意义（写请求头请放在 <see cref="Serialize"/> 里）。
    /// </para>
    /// <para>
    /// <b>需要响应头时读 <see cref="AxiosConfig.Response"/></b>（状态码 / 响应头 / 媒体类型 / 字符集）：
    /// 它只在响应方向有值，因此不必给本方法加 header 参数。
    /// </para>
    /// </summary>
    object? Deserialize(string text, Type type, AxiosConfig config);

    /// <summary>
    /// 把该格式的<b>字节</b>反序列化为业务对象（二进制格式的入口）。
    /// <para>
    /// 框架按响应 <c>Content-Type</c> 判定形态：文本类型（<c>text/*</c>、<c>+json</c> / <c>+xml</c> / <c>+yaml</c>…）
    /// 走文本入口；其余（octet-stream、image/png、protobuf…）走这里且<b>不做任何文本解码</b>，
    /// Protobuf / MessagePack / CBOR 因此可以零拷贝接入。
    /// </para>
    /// <para>
    /// 默认实现按 UTF-8 解码后转调文本入口，所以<b>文本格式的序列化器不需要实现本方法</b>
    /// （也兼容把 JSON 标成 <c>application/octet-stream</c> 的服务端）。
    /// </para>
    /// </summary>
    object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Deserialize(Encoding.UTF8.GetString(data.Span), type, config);
}
