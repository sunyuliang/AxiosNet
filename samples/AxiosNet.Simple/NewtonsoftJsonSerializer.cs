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

    public string Serialize(object? value, Type type, AxiosConfig config)
        => JsonConvert.SerializeObject(value, type, _settings);

    public object? Deserialize(string json, Type type, AxiosConfig config)
        => JsonConvert.DeserializeObject(json, type, _settings);

}
