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
/// net6.0 没有该 API 也没有 <c>TypeInfoResolver</c>，只能走 Type 重载，反射被关掉时同样给出可读异常。
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
    {
        // null 请求体与类型无关：不需要任何元数据，按 STJ 默认输出 null（否则空体会被误报为缺元数据）。
        if (value is null)
        {
            // 但 null 传给非可空值类型仍是非法调用：判定放在两个目标框架共用的这里，
            // 否则 net6.0 会由 STJ 的 Type 重载抛出 JsonException，与 net8.0 的异常类型不一致。
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new ArgumentNullException(nameof(value), $"类型 {type.FullName} 是非可空值类型，不能序列化 null。");
            }

            return "null";
        }

        return SerializeCore(value, type, AxiosJson.GetOptions(config));
    }

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
            if (current == typeof(object) && type != typeof(object))
            {
                // object 的元数据只描述 object：拿它序列化派生实例会按「未注册的运行时类型」报错，
                // 而且错误文本指向 STJ 内部，不如直接报缺元数据。
                break;
            }

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
    // net6.0 的 STJ 既没有 GetTypeInfo 也没有 TypeInfoResolver（.NET 7 才引入），反射重载是唯一路径：
    // 「反射是否被关掉」只能读与 .NET 8 属性同源的 AppContext 开关，据此给出与 net8.0 一致的可读错误。
    private const string ReflectionSwitchName = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 TypeInfoResolver，反射重载是唯一路径；反射被关掉时已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：仅在反射回退开启（JIT 或显式开启该开关）时可达，AOT 默认关闭时提前拒绝。")]
    private static string SerializeCore(object? value, Type type, JsonSerializerOptions options)
    {
        EnsureReflectionMetadata(type);
        return JsonSerializer.Serialize(value, type, options);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 TypeInfoResolver，反射重载是唯一路径；反射被关掉时已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：仅在反射回退开启（JIT 或显式开启该开关）时可达，AOT 默认关闭时提前拒绝。")]
    private static object? DeserializeCore(string json, Type type, JsonSerializerOptions options)
    {
        EnsureReflectionMetadata(type);
        return JsonSerializer.Deserialize(json, type, options);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "net6.0 无 TypeInfoResolver，反射重载是唯一路径；反射被关掉时已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：仅在反射回退开启（JIT 或显式开启该开关）时可达，AOT 默认关闭时提前拒绝。")]
    private static object? DeserializeUtf8Core(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
    {
        EnsureReflectionMetadata(type);
        return JsonSerializer.Deserialize(utf8Json, type, options);
    }

    /// <summary>
    /// 没有 resolver 时，反射是唯一路径：开关未设置视为开启（JIT 默认），关掉时按缺元数据拒绝。
    /// </summary>
    private static void EnsureReflectionMetadata(Type type)
    {
        if (!AppContext.TryGetSwitch(ReflectionSwitchName, out var enabled) || enabled)
        {
            return;
        }

        // 通用消息让调用方去设 TypeInfoResolver，而 net6.0 目标没有该属性：补一句这个目标上真正可做的选择。
        throw new NotSupportedException(
            AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message
            + "net6.0 目标没有 TypeInfoResolver API：请改用自定义 IContentSerializer，或把目标框架提升到 net8.0。");
    }
#endif
}
