using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AxiosNet.Metadata;

namespace AxiosNet.Reflection;

/// <summary>System.Text.Json 的反射回退：没有 <c>TypeInfoResolver</c> 时 STJ 只能用反射重载，反射被关掉（Native AOT）时在这里给出可读错误。</summary>
internal static class JsonSerializerJit
{
    /// <summary>net6 目标没有 <c>TypeInfoResolver</c>：直接走反射重载（调用方已确认允许动态代码）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    public static string SerializeLegacy(object? value, Type type, JsonSerializerOptions options)
        => JsonSerializer.Serialize(value, type, options);

    /// <summary>同一分支的字符串反序列化入口。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    public static object? DeserializeLegacy(string json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(json, type, options);

    /// <summary>同一分支的 UTF-8 字节反序列化入口（省一次字符串分配）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    public static object? DeserializeUtf8Legacy(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(utf8Json, type, options);

    /// <summary>同一分支的「写进现有 Utf8JsonWriter」入口（聚合正文按成员逐个写出）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    public static void SerializeToWriterLegacy(Utf8JsonWriter writer, object? value, Type type, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, type, options);

    /// <summary>同一分支的 UTF-8 字节序列化入口（省一次文本中转）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "仅在未配置 TypeInfoResolver 且 STJ 反射回退开启（JIT）时可达；AOT 下已在 EnsureReflectionMetadata 提前拒绝。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "同上：AOT 下反射被配置关闭，不会走到该重载。")]
    public static byte[] SerializeUtf8Legacy(object? value, Type type, JsonSerializerOptions options)
        => JsonSerializer.SerializeToUtf8Bytes(value, type, options);

#if NET8_0_OR_GREATER
    /// <summary>没有 resolver 时，STJ 只在反射回退开启（JIT）时才能解析。</summary>
    public static void EnsureReflectionMetadata(Type type)
    {
        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new NotSupportedException(AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message);
        }
    }
#else
    // net6.0 的 STJ 没有 TypeInfoResolver，反射重载是唯一路径；反射开关读与 .NET 8 同源的 AppContext 项。
    private const string ReflectionSwitchName = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    /// <summary>没有 resolver 时，反射是唯一路径：开关未设置视为开启（JIT 默认），关掉时按缺元数据拒绝。</summary>
    public static void EnsureReflectionMetadata(Type type)
    {
        if (!AppContext.TryGetSwitch(ReflectionSwitchName, out var enabled) || enabled)
        {
            return;
        }

        // net6.0 没有 TypeInfoResolver API，补一句该目标上可做的选择。
        throw new NotSupportedException(
            AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message
            + "net6.0 目标没有 TypeInfoResolver API：请改用自定义 IContentSerializer，或把目标框架提升到 net8.0。");
    }
#endif
}
