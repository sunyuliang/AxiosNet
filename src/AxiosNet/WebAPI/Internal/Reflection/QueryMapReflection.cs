using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AxiosNet.WebAPI.Internal.Reflection;

/// <summary>
/// 自定义字典的反射回退：只实现 <c>IReadOnlyDictionary&lt;string,T&gt;</c> 的字典按 <c>KeyValuePair</c> 的 Key / Value 读取。
/// 只在 JIT 下（动态代码可用时）可达；AOT 下由生成器或手工注册提供访问器。
/// </summary>
internal static class QueryMapReflection
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)> PairAccessors = new();

    /// <summary>把字典按键值对枚举出来（键为 <c>null</c> 的项跳过）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "仅对自定义只读字典的键值对类型读取 Key/Value；AOT 下需由消费方 root 类型所在程序集（README「Native AOT / 裁剪」）。")]
    public static IEnumerable<KeyValuePair<string, object?>> EnumeratePairs(object dictionary)
    {
        foreach (var item in (IEnumerable)dictionary)
        {
            var accessors = PairAccessors.GetOrAdd(item.GetType(), static type =>
            {
                var key = type.GetProperty("Key");
                var value = type.GetProperty("Value");
                if (key is null || value is null)
                {
                    throw new InvalidOperationException($"类型 {type.Name} 既不是 IDictionary，也不是键值对序列：无法作为查询参数映射使用。");
                }

                return (key, value);
            });

            yield return new KeyValuePair<string, object?>(
                accessors.Key.GetValue(item)?.ToString() ?? string.Empty,
                accessors.Value.GetValue(item));
        }
    }
}
