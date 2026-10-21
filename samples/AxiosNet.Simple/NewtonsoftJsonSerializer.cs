using AxiosNet;
using AxiosNet.Serialization;
using Newtonsoft.Json;

namespace AxiosNet.Simple;

/// <summary>
/// 把 Newtonsoft.Json 接入 AxiosNet 的适配器（示例用）：实现 <see cref="IContentSerializer"/>
/// 的两个方法即可整体替换默认的 System.Text.Json。
/// <para>
/// 与默认实现的差异：属性命名默认保持原样（PascalCase），而 System.Text.Json 的 Web 默认输出 camelCase。
/// </para>
/// <para>
/// 注意 <c>dynamic</c> 与非泛型响应由库自己处理（<c>ExpandoObject</c> / <c>JsonElement</c>），
/// 不经过序列化器 —— 所以这里的换引擎只影响「业务对象」的编解码。
/// </para>
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
