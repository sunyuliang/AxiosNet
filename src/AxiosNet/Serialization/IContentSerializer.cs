using System.Text;

namespace AxiosNet.Serialization;

/// <summary>
/// 请求体 / 响应体的<b>格式编解码</b>抽象：JSON（库内默认，System.Text.Json）、XML（库内
/// <see cref="XmlContentSerializer"/>）、YAML、CSV、Protobuf、MessagePack…… 都通过这个接口接入。
/// <para>
/// 边界只有一条：<b>业务对象 ↔ 该格式的内容</b>。库的职责是分派，不是解释：
/// </para>
/// <list type="bullet">
/// <item>请求：<c>string</c> / <c>byte[]</c> / <c>Stream</c> / <c>HttpContent</c> 原样发送（只套媒体类型），
/// 表单与 multipart 是独立的编码通道（由 <see cref="AxiosConfig.ContentType"/> 决定，不经过本接口），
/// 基础数据类型（数值、布尔、枚举、Guid、日期时间、Uri…）由框架直接取其文本，
/// 其余（对象、<b>字典</b>、dynamic、集合）把 <see cref="AxiosConfig.Data"/> 交给 <see cref="Serialize"/>。</item>
/// <item>响应：<c>byte[]</c> / <c>Stream</c> / <c>string</c> / 基础数据类型（数值、布尔、枚举、Guid、
/// 日期时间、Uri…）由框架直接转换；只有<b>业务对象</b>才调用 <c>Deserialize</c>。</item>
/// <item>响应体的形态（文本 / 二进制）由框架按响应 <c>Content-Type</c> 判定：
/// 文本类型走 <see cref="Deserialize(string, Type, AxiosConfig)"/>，
/// 非文本类型走 <see cref="Deserialize(ReadOnlyMemory{byte}, Type, AxiosConfig)"/>，且不做文本解码。</item>
/// </list>
/// <para>
/// 因此库不要求任何「通用数据树」（<c>JsonElement</c> 之类）作为交换格式，也不感知格式细节：
/// <c>Deserialize</c> 返回什么对象，<c>Response.Data</c> 就是什么。
/// </para>
/// <para>
/// 约定：实现会被并发请求共享，必须无状态或线程安全；抛出的异常由库统一包装成
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
    /// 本格式对应的媒体类型（<c>Content-Type</c> 主值，如 <c>application/json</c>、
    /// <c>application/xml</c>、<c>application/x-protobuf</c>）。
    /// <para>
    /// 用途只有一个：<b>请求方向</b>调用方没有显式设置 <see cref="AxiosConfig.ContentType"/> 时，
    /// 库把这个值写进请求头 —— 与 axios 默认 <c>transformRequest</c> 里的
    /// <c>headers.setContentType('application/json', false)</c> 同构（已有值不覆盖）。
    /// 于是「注入一个 XML / protobuf 序列化器」就等于「整条链路换格式」，
    /// 不必再手动把头和正文配对。
    /// </para>
    /// <para>
    /// 默认 <c>null</c> = <b>不自报</b>，沿用 <see cref="AxiosConfig.ContentType"/>（默认 JSON）。
    /// 调用方显式设置过 Content-Type 时永远以调用方为准，本值不覆盖它——
    /// 那是调用方对自己服务端的判断，库不该替它改主意。
    /// </para>
    /// <para>
    /// 响应方向不看本值：解析用哪个序列化器由<b>响应的</b> <c>Content-Type</c> 决定（头是权威）。
    /// </para>
    /// </summary>
    string? MediaType => null;

    /// <summary>
    /// 把业务对象序列化为该格式的文本。
    /// <para>
    /// 库会按 <see cref="AxiosConfig.RequestEncoding"/> 把文本编码成字节并补 Content-Type 的 charset。
    /// 二进制格式请由调用方自行序列化成 <c>byte[]</c> 作为 <see cref="AxiosConfig.Data"/> 传入
    /// （原样发送），本接口只承载文本。
    /// </para>
    /// <para>
    /// 只会收到「对象 / 字典 / dynamic / 集合」；<b>基础数据类型不会进来</b>——
    /// 框架直接取其文本发送（裸标量没有「格式」可言，XML / YAML 也无法表达无名的根标量）。
    /// </para>
    /// <para>
    /// <b>可以写请求头</b>：<paramref name="config"/> 是本次请求独占的副本（<c>Defaults.Clone()</c>，
    /// <see cref="AxiosConfig.Headers"/> 也复制了一份），写的头会在请求体构造完成后统一应用，
    /// 因此能覆盖框架按 <see cref="AxiosConfig.ContentType"/> 生成的默认头，
    /// 且不会污染实例默认值或其它并发请求。适合与格式强绑定的头
    /// （<c>Content-Type</c>、<c>Content-MD5</c>、<c>X-Checksum</c>）—— 这正是 axios
    /// <c>transformRequest(data, headers)</c> 里 <c>headers.setContentType(...)</c> 的等价能力。
    /// 例外：请求体是 multipart 时 <c>Content-Type</c> 不会被覆盖（否则正文的 boundary 无法解析）。
    /// </para>
    /// <para>
    /// ⚠️ 反过来，在方法里给 <see cref="AxiosConfig.ContentType"/> 赋值<b>对本次请求无效</b>：
    /// 请求体已经按赋值前的值构造完毕，改它是个静默的空操作。声明格式请用 <see cref="MediaType"/>，
    /// 或写 <c>config.Headers.Set("Content-Type", ...)</c>。
    /// 与格式无关的头（追踪 ID、租户）请用 <see cref="AxiosConfig.TransformRequest"/>：
    /// 时机更早、语义更清楚，也不会绑死在某个序列化器上。
    /// </para>
    /// </summary>
    string Serialize(object? value, Type type, AxiosConfig config);

    /// <summary>
    /// 把该格式的文本反序列化为业务对象。返回值会原样进入 <c>Response.Data</c>。
    /// <para>只有「目标类型是业务对象」且响应为<b>文本</b>（含无 Content-Type）时才会被调用；
    /// 基础数据类型由库直接解析，不经过这里。</para>
    /// <para>
    /// 响应方向：请求头早已发出，此时改 <see cref="AxiosConfig.Headers"/> 没有任何意义
    /// （写请求头请放在 <see cref="Serialize"/> 里）。
    /// </para>
    /// <para>
    /// <b>需要响应头时读 <see cref="AxiosConfig.Response"/></b>（状态码 / 响应头 / 媒体类型 / 字符集）：
    /// 它只在响应方向有值，因此不必给本方法加一个 header 参数。典型用途是按
    /// <c>X-Schema-Version</c> 之类的头选择解析规则。
    /// </para>
    /// </summary>
    object? Deserialize(string text, Type type, AxiosConfig config);

    /// <summary>
    /// 把该格式的<b>字节</b>反序列化为业务对象（二进制格式的入口）。返回值会原样进入 <c>Response.Data</c>。
    /// <para>
    /// 框架按响应 <c>Content-Type</c> 判定形态：文本类型（<c>text/*</c>、<c>application/json</c>、
    /// <c>+json</c> / <c>+xml</c> / <c>+yaml</c>、XML、YAML、CSV…）走文本入口；
    /// 其余（<c>application/octet-stream</c>、<c>image/png</c>、<c>application/x-protobuf</c>…）
    /// 走这里，并且<b>不做任何文本解码</b>——Protobuf / MessagePack / CBOR / Avro 等格式因此可以零拷贝接入。
    /// </para>
    /// <para>
    /// 默认实现按 UTF-8 解码后转调 <see cref="Deserialize(string, Type, AxiosConfig)"/>，
    /// 所以<b>文本格式的序列化器（JSON / XML / YAML）不需要实现本方法</b>。
    /// 这个宽松默认还有一个现实理由：不少服务端把 JSON 标成 <c>application/octet-stream</c>。
    /// </para>
    /// </summary>
    object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Deserialize(Encoding.UTF8.GetString(data.Span), type, config);
}
