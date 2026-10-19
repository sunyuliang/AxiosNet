using AxiosNet;
using AxiosNet.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AxiosNet.Simple;

/// <summary>
/// 把 Newtonsoft.Json 接入 AxiosNet 的适配器（示例用）：
/// 实现 <see cref="IAxiosJsonSerializer"/> 的四个方法即可整体替换默认的 System.Text.Json。
/// <para>
/// 与默认实现的差异：
/// <list type="bullet">
/// <item>属性命名默认保持原样（PascalCase），而 System.Text.Json 的 Web 默认会输出 camelCase；</item>
/// <item>dynamic 响应的 Data 是 <see cref="JObject"/>/<see cref="JArray"/>，同样支持 <c>data.UserName</c> 访问；</item>
/// <item>非泛型响应的文档形态是 <see cref="JToken"/>，不再是 <c>JsonElement</c>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class NewtonsoftJsonSerializer : IAxiosJsonSerializer
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

    public object? ParseDynamic(string json, AxiosConfig config)
        => JsonConvert.DeserializeObject<JToken>(json, _settings);

    public object ParseDocument(string json, AxiosConfig config) => JToken.Parse(json);
}
