using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：
/// 使用 <see cref="AxiosConfig.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// <para>
/// <c>dynamic</c> 展开与非泛型响应的 <c>JsonElement</c> 形态由库自己处理，不经过本类。
/// </para>
/// <para>
/// Native AOT / 裁剪：内部使用 STJ 的反射重载，AOT 下必须由消费方通过
/// <see cref="AxiosConfig.JsonSerializerOptions"/> 的 <c>TypeInfoResolver</c> 提供源生成上下文，
/// 否则运行时抛 <c>NotSupportedException</c>。
/// </para>
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

    /// <summary>
    /// 二进制入口：默认按 UTF-8 字节反序列化，不做 <c>byte[] → string</c> 中转。
    /// 触达路径是「Content-Type 声明非文本」（如服务端把 JSON 标成 <c>application/octet-stream</c>）。
    /// <para>
    /// 与接口默认实现（经 <c>ResponseTextDecoder</c> 解码）保持同一语义：声明了非 UTF-8
    /// <c>charset</c> 或调用方强制了 <see cref="AxiosConfig.ResponseEncoding"/> 时先按该编码解码；
    /// 否则走 UTF-8 快路径，并按 BOM 处理 UTF-16 / UTF-32 变体。
    /// net6.0 的 <c>JsonSerializer.Deserialize(ReadOnlySpan&lt;byte&gt;)</c> 自己不剥 BOM，
    /// 带 BOM 的正文会被首字节 0xEF 判成非法 JSON 起始；UTF-16 / UTF-32 的 BOM（首字节 0xFF / 0xFE）
    /// 更是直接无法按 UTF-8 解析。
    /// </para>
    /// </summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
    {
        var span = data.Span;

        // 非文本 Content-Type 也可能声明 charset（或调用方强制了 ResponseEncoding）。
        // 接口默认实现会先按这套优先级解码，这里必须保持一致；否则同一份字节走
        // 「text/json」能读对、走「application/octet-stream」却按 UTF-8 解析失败。
        var forced = config.ResponseEncoding;
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
