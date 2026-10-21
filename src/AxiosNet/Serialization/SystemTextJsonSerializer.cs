using System.Text.Json;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：
/// 序列化 / 反序列化使用 <see cref="AxiosConfig.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// <para>
/// <c>dynamic</c> 展开与非泛型响应的 <c>JsonElement</c> 形态由库自己处理，不经过本类
/// （它们是「无目标类型」的形态，不属于业务对象反序列化）。
/// </para>
/// </summary>
public sealed class SystemTextJsonSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享。</summary>
    public static SystemTextJsonSerializer Instance { get; } = new();

    private SystemTextJsonSerializer()
    {
    }

    /// <summary><c>application/json</c>（与库的 <see cref="AxiosConfig.ContentType"/> 默认值一致）。</summary>
    public string? MediaType => AxiosContentType.Json;

    public string Serialize(object? value, Type type, AxiosConfig config)
        => JsonSerializer.Serialize(value, type, AxiosJson.GetOptions(config));

    public object? Deserialize(string json, Type type, AxiosConfig config)
        => JsonSerializer.Deserialize(json, type, AxiosJson.GetOptions(config));

    /// <summary>
    /// 二进制入口：直接走 UTF-8 字节反序列化，不做 <c>byte[] → string</c> 中转。
    /// 触达路径是「Content-Type 声明非文本」（如服务端把 JSON 标成 <c>application/octet-stream</c>）。
    /// </summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => JsonSerializer.Deserialize(data.Span, type, AxiosJson.GetOptions(config));
}
