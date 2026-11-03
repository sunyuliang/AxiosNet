using AxiosNet;
using AxiosNet.Serialization;
using Newtonsoft.Json;

namespace AxiosNet.Simple;

/// <summary>
/// 把 Newtonsoft.Json 接入 AxiosNet 的适配器（示例用）：实现 <see cref="IContentSerializer"/>
/// 的两个方法即可整体替换默认的 System.Text.Json，属性命名保持原样（PascalCase）。
/// <c>dynamic</c> 与非泛型响应由库自己处理（<c>ExpandoObject</c> / <c>JsonElement</c>），不经过序列化器。
/// </summary>
public sealed class NewtonsoftJsonSerializer : IContentSerializer
{
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftJsonSerializer(JsonSerializerSettings? settings = null)
    {
        _settings = settings ?? new JsonSerializerSettings
        {
            // 默认值即可：保留属性名、ISO 日期、忽略未知成员
            DateParseHandling = DateParseHandling.DateTime,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
    }

    /// <summary>用 Newtonsoft.Json 序列化，保留属性名原名以对齐 PascalCase 契约。</summary>
    public string Serialize(object? value, Type type, AxiosConfig config)
        => JsonConvert.SerializeObject(value, type, _settings);

    /// <summary>用 Newtonsoft.Json 反序列化；未知成员忽略。</summary>
    public object? Deserialize(string json, Type type, AxiosConfig config)
        => JsonConvert.DeserializeObject(json, type, _settings);

}
