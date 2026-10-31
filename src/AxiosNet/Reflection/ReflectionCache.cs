using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AxiosNet.Reflection;

/// <summary>
/// 属性级反射元数据缓存：按 <see cref="PropertyInfo"/> 缓存字段名与忽略标记，避免每请求重复读特性。
/// 只有 JIT 反射路径（形状展平、请求模型声明读取）会用到。
/// </summary>
internal static class ReflectionCache
{
    /// <summary>表单 / multipart 的字段名（<c>[JsonPropertyName]</c> 优先，否则属性名）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, string> FieldNames = new();

    /// <summary>是否整体忽略（<c>[JsonIgnore(Condition = JsonIgnoreCondition.Always)]</c>）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, bool> IgnoredProperties = new();

    /// <summary>表单 / multipart 字段名：<c>[JsonPropertyName]</c> 优先，否则属性名（按属性缓存）。</summary>
    public static string GetFieldName(PropertyInfo property)
        => FieldNames.GetOrAdd(property, static p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);

    /// <summary>是否整体忽略：只认 <c>[JsonIgnore(Always)]</c>（条件忽略在展平时无法求值，按参与处理）。</summary>
    public static bool IsIgnoredProperty(PropertyInfo property)
        => IgnoredProperties.GetOrAdd(property, static p =>
        {
            var attribute = p.GetCustomAttribute<JsonIgnoreAttribute>();
            return attribute is not null && attribute.Condition == JsonIgnoreCondition.Always;
        });
}
