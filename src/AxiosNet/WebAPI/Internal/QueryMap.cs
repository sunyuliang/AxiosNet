using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AxiosNet.WebAPI.Internal;

/// <summary>字符串键字典的识别与枚举：供查询参数平铺与 <see cref="HeaderCollectionAttribute"/> 共用。</summary>
internal static class QueryMap
{
    /// <summary>是否为字符串键字典（<c>IDictionary</c> 或 <c>IDictionary&lt;string,T&gt;</c> / <c>IReadOnlyDictionary&lt;string,T&gt;</c>）。</summary>
    public static bool IsMapType(Type type)
        => typeof(IDictionary).IsAssignableFrom(type) || FindStringKeyedPair(type, valueType: null) is not null;

    /// <summary>是否为「字符串键 + 字符串值」字典（<see cref="HeaderCollectionAttribute"/> 的要求）。</summary>
    public static bool IsStringMapType(Type type)
        => FindStringKeyedPair(type, typeof(string)) is not null;

    /// <summary>按键值对枚举字典；键统一转成字符串，值保持原始对象（由调用方决定怎么用）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "仅对自定义只读字典的键值对类型读取 Key/Value；AOT 下需由消费方 root 类型所在程序集（README「Native AOT / 裁剪」）。")]
    public static IEnumerable<KeyValuePair<string, object?>> Enumerate(object dictionary)
    {
        switch (dictionary)
        {
            case IDictionary<string, object?> objectMap:
                foreach (var entry in objectMap)
                {
                    yield return entry;
                }

                break;

            case IReadOnlyDictionary<string, object?> objectReadOnlyMap:
                foreach (var entry in objectReadOnlyMap)
                {
                    yield return entry;
                }

                break;

            case IDictionary map:
                foreach (DictionaryEntry entry in map)
                {
                    if (entry.Key is not null)
                    {
                        yield return new KeyValuePair<string, object?>(entry.Key.ToString()!, entry.Value);
                    }
                }

                break;

            default:
                // 只实现 IReadOnlyDictionary<string,T> 的自定义字典：按 KeyValuePair 读取
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

                    yield return new KeyValuePair<string, object?>(accessors.Key.GetValue(item)?.ToString() ?? string.Empty, accessors.Value.GetValue(item));
                }

                break;
        }
    }

    /// <summary>
    /// 在类型自身与接口里找 <c>IDictionary&lt;string,T&gt;</c> / <c>IReadOnlyDictionary&lt;string,T&gt;</c>；<paramref name="valueType"/> 非 null 时还要求值类型相符。
    /// </summary>
    private static Type? FindStringKeyedPair(Type type, Type? valueType)
    {
        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition != typeof(IDictionary<,>) && definition != typeof(IReadOnlyDictionary<,>))
            {
                continue;
            }

            var arguments = candidate.GetGenericArguments();
            if (arguments[0] == typeof(string) && (valueType is null || arguments[1] == valueType))
            {
                return candidate;
            }
        }

        return null;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "字典类型由调用方运行时提供；AOT 下需由消费方 root 类型所在程序集（README「Native AOT / 裁剪」）。")]
    private static IEnumerable<Type> EnumerateSelfAndInterfaces(Type type)
    {
        yield return type;
        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }
    }

    private static readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)> PairAccessors = new();
}
