using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Reflection;

/// <summary>
/// System.Text.Json 的反射回退：没有 <c>TypeInfoResolver</c> 时 STJ 只能用反射重载，
/// 反射被关掉（Native AOT）时在这里给出可读错误而不是 STJ 内部的异常。
/// </summary>
internal static class JsonSerializerJit
{
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
    // net6.0 的 STJ 既没有 GetTypeInfo 也没有 TypeInfoResolver（.NET 7 才引入），反射重载是唯一路径：
    // 「反射是否被关掉」只能读与 .NET 8 属性同源的 AppContext 开关，据此给出与 net8.0 一致的可读错误。
    private const string ReflectionSwitchName = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    /// <summary>没有 resolver 时，反射是唯一路径：开关未设置视为开启（JIT 默认），关掉时按缺元数据拒绝。</summary>
    public static void EnsureReflectionMetadata(Type type)
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
