using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 默认的 System.Text.Json 实现：
/// 序列化/反序列化使用 <see cref="AxiosConfig.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>），
/// dynamic 展开与文档解析复用库内已有的 <see cref="DynamicJson"/> / <see cref="JsonDocument"/>。
/// </summary>
public sealed class SystemTextJsonSerializer : IAxiosJsonSerializer
{
    /// <summary>无状态单例，可安全并发共享。</summary>
    public static SystemTextJsonSerializer Instance { get; } = new();

    private SystemTextJsonSerializer()
    {
    }

    public string Serialize(object? value, Type type, AxiosConfig config)
        => JsonSerializer.Serialize(value, type, AxiosJson.GetOptions(config));

    public object? Deserialize(string json, Type type, AxiosConfig config)
        => JsonSerializer.Deserialize(json, type, AxiosJson.GetOptions(config));

    public object? ParseDynamic(string json, AxiosConfig config) => DynamicJson.Parse(json);

    public object ParseDocument(string json, AxiosConfig config)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 序列化为 <see cref="JsonElement"/> 树（表单/multipart 展平需要 JSON 树语义）。
    /// 与 <see cref="Serialize"/> 使用同一套选项，保证命名策略/转换器一致。
    /// </summary>
    public JsonElement ToElement(object? value, Type type, AxiosConfig config)
        => JsonSerializer.SerializeToElement(value, type, AxiosJson.GetOptions(config));
}
