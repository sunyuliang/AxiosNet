using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：用 <see cref="AxiosConfig.SerializationSettings.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>），
/// Native AOT / 裁剪下必须由消费方提供源生成上下文（<c>TypeInfoResolver</c>）。
/// </summary>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "STJ 反射重载；AOT 下由消费方通过 JsonSerializerOptions.TypeInfoResolver 提供源生成上下文（README「Native AOT / 裁剪」）。")]
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

    /// <summary>二进制入口：按 charset / BOM 处理后再反序列化，不做 <c>byte[] → string</c> 中转。</summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
    {
        var span = data.Span;

        // 非文本 Content-Type 也可能声明 charset：与接口默认实现保持同一优先级。
        var forced = config.SerializationOrNull?.ResponseEncoding;
        var charSet = config.Response?.CharSet;
        if (forced is not null
            || (ResponseTextDecoder.ResolveFromCharset(charSet) is { } declared
                && declared.CodePage != Encoding.UTF8.CodePage))
        {
            return Deserialize(ResponseTextDecoder.Decode(span, charSet, forced), type, config);
        }

        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }
        else if (ResponseTextDecoder.DetectFromBom(span) is { } bomEncoding)
        {
            return Deserialize(ResponseTextDecoder.Decode(span, charSet: null, forced: bomEncoding), type, config);
        }

        return JsonSerializer.Deserialize(span, type, AxiosJson.GetOptions(config));
    }
}
