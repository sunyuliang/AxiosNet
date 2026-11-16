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
    /// <summary>
    /// 缓存的目标条目上限：到这条线只是「记满」，继续吸收到 <see cref="ClearThreshold"/> 才整体清空。
    /// </summary>
    /// <remarks>
    /// 用「上限 + 清空」而不是 <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>：
    /// 后者在 netstandard2.0 上没有原子 <c>AddOrUpdate</c>，且反射不保证同一属性返回同一个 <see cref="PropertyInfo"/> 实例。
    /// 清空后旧类型的强引用消失，插件 / 可回收 <c>AssemblyLoadContext</c> 下的程序集即可回收。
    /// </remarks>
    private const int MaxEntries = 4096;

    /// <summary>
    /// 触发整体清空的条目数（<see cref="MaxEntries"/> 的 2 倍）：工作集略超上限时避免「每来一个新属性就清一次」
    /// 的稳态抖动（清空的 O(n) 成本摊到随后再插入的 n 条上，摊还仍是每插入一次）。
    /// </summary>
    private const int ClearThreshold = MaxEntries * 2;

    /// <summary>表单 / multipart 的字段名（<c>[JsonPropertyName]</c> 优先，否则属性名）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, string> FieldNames = new();

    /// <summary>是否整体忽略（<c>[JsonIgnore(Condition = JsonIgnoreCondition.Always)]</c>）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, bool> IgnoredProperties = new();

    /// <summary>表单 / multipart 字段名：<c>[JsonPropertyName]</c> 优先，否则属性名（按属性缓存）。</summary>
    /// <param name="property">属性。</param>
    /// <returns>展平后使用的字段名。</returns>
    public static string GetFieldName(PropertyInfo property)
    {
        if (FieldNames.TryGetValue(property, out var cached))
        {
            return cached;
        }

        var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
        Trim(FieldNames);
        FieldNames[property] = name;
        return name;
    }

    /// <summary>是否整体忽略：只认 <c>[JsonIgnore(Always)]</c>（条件忽略在展平时无法求值，按参与处理）。</summary>
    /// <param name="property">属性。</param>
    /// <returns>需要整体忽略返回 <c>true</c>。</returns>
    public static bool IsIgnoredProperty(PropertyInfo property)
    {
        if (IgnoredProperties.TryGetValue(property, out var cached))
        {
            return cached;
        }

        var attribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
        var ignored = attribute is not null && attribute.Condition == JsonIgnoreCondition.Always;
        Trim(IgnoredProperties);
        IgnoredProperties[property] = ignored;
        return ignored;
    }

    /// <summary>超出上限时整体清空（见 <see cref="MaxEntries"/> 与 <see cref="ClearThreshold"/> 的理由）。</summary>
    /// <typeparam name="TValue">缓存值类型。</typeparam>
    /// <param name="cache">要检查的缓存。</param>
    private static void Trim<TValue>(ConcurrentDictionary<PropertyInfo, TValue> cache)
    {
        // Count 会取全部锁，只在未命中时调一次；命中路径完全不碰它。
        if (cache.Count >= ClearThreshold)
        {
            cache.Clear();
        }
    }
}
