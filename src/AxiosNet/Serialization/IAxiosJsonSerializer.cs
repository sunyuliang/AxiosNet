namespace AxiosNet.Serialization;

/// <summary>
/// JSON 序列化器的可插拔接口：把「请求体对象 → JSON 文本 / JSON 文本 → 响应对象」从库里解耦，
/// 便于接入 Newtonsoft.Json、源生成序列化器等。
/// <para>
/// 约定：
/// <list type="bullet">
/// <item><see cref="Serialize"/> 与 <see cref="ParseDocument"/> 必须产出/接受标准 JSON 文本；</item>
/// <item>实现应保持无状态或线程安全（同一实例会被并发请求共享）；</item>
/// <item>抛出的异常会被库包装成 <c>ERR_BAD_RESPONSE</c> / <c>ERR_BAD_REQUEST</c>，
/// 不需要自己构造 <see cref="AxiosException"/>。</item>
/// </list>
/// </para>
/// </summary>
public interface IAxiosJsonSerializer
{
    /// <summary>把请求体对象序列化为 JSON 文本。</summary>
    string Serialize(object? value, Type type, AxiosConfig config);

    /// <summary>把 JSON 文本反序列化为目标类型。</summary>
    object? Deserialize(string json, Type type, AxiosConfig config);

    /// <summary>
    /// 把 JSON 文本展开为支持动态成员访问的对象图（对应 <see cref="ResponseType.Dynamic"/>），
    /// 例如 <c>ExpandoObject</c> + <c>List&lt;object?&gt;</c>。
    /// </summary>
    object? ParseDynamic(string json, AxiosConfig config);

    /// <summary>
    /// 把 JSON 文本解析为「文档形态」对象（非泛型响应 / <c>JsonElement</c> 目标）。
    /// 默认实现返回 <see cref="System.Text.Json.JsonElement"/>。
    /// </summary>
    object ParseDocument(string json, AxiosConfig config);
}
