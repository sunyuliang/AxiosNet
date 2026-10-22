using System.Text.Json;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：
/// 使用 <see cref="AxiosConfig.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// <para>
/// <c>dynamic</c> 展开与非泛型响应的 <c>JsonElement</c> 形态由库自己处理，不经过本类。
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
    /// 二进制入口：直接按 UTF-8 字节反序列化，不做 <c>byte[] → string</c> 中转。
    /// 触达路径是「Content-Type 声明非文本」（如服务端把 JSON 标成 <c>application/octet-stream</c>）。
    /// <para>
    /// 与接口默认实现（经 <c>ResponseTextDecoder</c> 解码）保持同一语义：剥掉 UTF-8 BOM 后再交给 STJ
    /// —— net6.0 的 <c>JsonSerializer.Deserialize(ReadOnlySpan&lt;byte&gt;)</c> 自己不剥 BOM，
    /// 带 BOM 的正文会被首字节 0xEF 判成非法 JSON 起始。
    /// </para>
    /// </summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
    {
        var span = data.Span;

        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return JsonSerializer.Deserialize(span, type, AxiosJson.GetOptions(config));
    }
}
