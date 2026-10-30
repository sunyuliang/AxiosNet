using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：用 <see cref="AxiosConfig.SerializationSettings.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// net8.0 目标显式走 <c>JsonSerializerOptions.GetTypeInfo</c>（AOT 下缺元数据时给出可读异常）；
/// net6.0 没有该 API，只能走 Type 重载，Native AOT 下需由消费方提供源生成上下文。
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

    /// <inheritdoc />
    public string Serialize(object? value, Type type, AxiosConfig config)
        => SerializeCore(value, type, AxiosJson.GetOptions(config));

    /// <inheritdoc />
    public object? Deserialize(string json, Type type, AxiosConfig config)
        => DeserializeCore(json, type, AxiosJson.GetOptions(config));

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

        return DeserializeUtf8Core(span, type, AxiosJson.GetOptions(config));
    }

#if NET8_0_OR_GREATER
    private static string SerializeCore(object? value, Type type, JsonSerializerOptions options)
    {
        // 没有 TypeInfoResolver 时保持旧的反射重载语义（含匿名类型）；AOT 下反射默认关闭，提前给出可读错误。
        if (options.TypeInfoResolver is null)
        {
            EnsureReflectionMetadata(type);
            return SerializeLegacy(value, type, options);
        }

        return JsonSerializer.Serialize(value, Resolve(options, type));
    }

    private static object? DeserializeCore(string json, Type type, JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null)
        {
            EnsureReflectionMetadata(type);
            return DeserializeLegacy(json, type, options);
        }

        return JsonSerializer.Deserialize(json, Resolve(options, type));
    }

    private static object? DeserializeUtf8Core(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null)
        {
            EnsureReflectionMetadata(type);
            return DeserializeUtf8Legacy(utf8Json, type, options);
        }

        return JsonSerializer.Deserialize(utf8Json, Resolve(options, type));
    }

    /// <summary>
    /// 取声明类型的 <see cref="JsonTypeInfo"/>；缺元数据时按「最近祖先」回退。
    /// 只有在调用方显式配置了 <see cref="JsonSerializerOptions.TypeInfoResolver"/>（源生成 / 组合解析器）时才走这里。
    /// </summary>
    private static JsonTypeInfo Resolve(JsonSerializerOptions options, Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            try
            {
                return options.GetTypeInfo(current);
            }
            catch (NotSupportedException)
            {
                // 继续向基类回退
            }
        }

        throw new NotSupportedException(AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message);
    }

    /// <summary>没有 resolver 时，STJ 只在反射回退开启（JIT）时才能解析。</summary>
    private static void EnsureReflectionMetadata(Type type)
    {
        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new NotSupportedException(AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    private static string SerializeLegacy(object? value, Type type, JsonSerializerOptions options)
        => JsonSerializer.Serialize(value, type, options);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    private static object? DeserializeLegacy(string json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(json, type, options);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    private static object? DeserializeUtf8Legacy(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(utf8Json, type, options);
#else
    // net6.0 没有 GetTypeInfo：AOT 下由消费方的 JsonSerializerContext 提供元数据。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 GetTypeInfo API；AOT 下由消费方通过 TypeInfoResolver 提供源生成上下文。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：元数据由 TypeInfoResolver 提供，不依赖运行时代码生成。")]
    private static string SerializeCore(object? value, Type type, JsonSerializerOptions options)
        => JsonSerializer.Serialize(value, type, options);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 GetTypeInfo API；AOT 下由消费方通过 TypeInfoResolver 提供源生成上下文。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：元数据由 TypeInfoResolver 提供，不依赖运行时代码生成。")]
    private static object? DeserializeCore(string json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(json, type, options);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 GetTypeInfo API；AOT 下由消费方通过 TypeInfoResolver 提供源生成上下文。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：元数据由 TypeInfoResolver 提供，不依赖运行时代码生成。")]
    private static object? DeserializeUtf8Core(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(utf8Json, type, options);
#endif
}
