using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 请求体 / 响应体的<b>格式编解码</b>抽象：JSON（默认）、XML、YAML、Protobuf、MessagePack 等都通过它接入，边界只有业务对象 ↔ 该格式的内容。
/// 实现会被并发请求共享，必须无状态或线程安全；string / byte[] / Stream / 表单 / multipart / 基础类型都由库直接处理，不经过本接口。
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
    /// 本格式对应的媒体类型（<c>Content-Type</c> 主值，如 <c>application/json</c>）。
    /// 请求方向未显式设置 <see cref="AxiosConfig.ContentType"/> 时由库写进请求头；默认 <c>null</c> 表示不自报。
    /// </summary>
    string? MediaType => null;

    /// <summary>
    /// 把业务对象序列化为该格式的文本，由库按 <see cref="AxiosConfig.SerializationSettings.RequestEncoding"/> 编码成字节并补 charset。
    /// 二进制格式请覆写 <see cref="SerializeToBytes"/>，本方法实现成抛 <see cref="NotSupportedException"/>。
    /// </summary>
    string Serialize(object? value, Type type, AxiosConfig config);

    /// <summary>
    /// 把业务对象序列化为该格式的<b>字节</b>（二进制格式的请求出口）；返回 <c>null</c>（默认）表示改用 <see cref="Serialize"/> 的文本。
    /// 返回的内存会被直接交给请求内容，生命周期必须覆盖到整条请求结束。
    /// </summary>
    ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config) => null;

    /// <summary>
    /// 把该格式的文本反序列化为业务对象，返回值原样进入 <c>Response.Data</c>；只有业务对象且响应为文本时才会被调用。
    /// 需要响应头时读 <see cref="AxiosConfig.Response"/>。
    /// </summary>
    object? Deserialize(string text, Type type, AxiosConfig config);

    /// <summary>
    /// 把该格式的<b>字节</b>反序列化为业务对象（二进制格式的入口），不做文本解码。
    /// 默认实现按响应字符集解码后转调文本入口，因此文本格式的序列化器不需要实现本方法。
    /// </summary>
    object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Deserialize(ResponseTextDecoder.Decode(data.Span, config.Response?.CharSet, config.SerializationOrNull?.ResponseEncoding), type, config);
}
