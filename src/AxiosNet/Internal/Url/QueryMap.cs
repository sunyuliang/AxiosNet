using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Internal.Url;

/// <summary>字符串键字典的识别与枚举：查询参数平铺与头集合参数共用。</summary>
internal static class QueryMap
{
    /// <summary>是否为字符串键字典（<c>IDictionary</c> 或 <c>IDictionary&lt;string,T&gt;</c> / <c>IReadOnlyDictionary&lt;string,T&gt;</c>）。</summary>
    public static bool IsMapType(Type type)
        => typeof(IDictionary).IsAssignableFrom(type) || FindStringKeyedPair(type, valueType: null) is not null;

    /// <summary>是否为「字符串键 + 字符串值」字典（头集合参数要求）。</summary>
    public static bool IsStringMapType(Type type)
        => FindStringKeyedPair(type, typeof(string)) is not null;

    /// <summary>是否能按字典枚举（<see cref="Enumerate"/> 的任意一条通道可用即算）。</summary>
    public static bool CanEnumerate(object dictionary)
        => dictionary is IDictionary
            || dictionary is IDictionary<string, object?>
            || dictionary is IReadOnlyDictionary<string, object?>
            || TryGetAccessor(dictionary, out _)
            || MapTypes.GetOrAdd(dictionary.GetType(), static type => IsMapType(type));

    /// <summary>是否能「不抛错地」按字典枚举（回收 / 遍历路径用它决定要不要下钻）；AOT 下需反射的形态不计入。</summary>
    public static bool CanEnumerateBestEffort(object dictionary)
        => dictionary is IDictionary
            || dictionary is IDictionary<string, object?>
            || dictionary is IReadOnlyDictionary<string, object?>
            || TryGetAccessor(dictionary, out _)
            || (!RuntimeSupport.ForceNoDynamicCode
                && RuntimeFeature.IsDynamicCodeSupported
                && MapTypes.GetOrAdd(dictionary.GetType(), static type => IsMapType(type)));

    /// <summary>取自定义字典的枚举访问器：按具体类型查注册表，再沿接口查；结果按具体类型缓存。</summary>
    /// <remarks>注册表更新后 <see cref="Invalidate"/> 推进纪元，旧结果（含「没有」）不再命中。</remarks>
    public static bool TryGetAccessor(object dictionary, [NotNullWhen(true)] out MapAccessor? accessor)
    {
        var type = dictionary.GetType();
        var epoch = Volatile.Read(ref _epoch);
        if (ResolvedMaps.TryGetValue(type, out var cached) && cached.Epoch == epoch)
        {
            accessor = cached.Accessor;
            return accessor is not null;
        }

        accessor = ResolveAccessor(type);

        // 纪元变了就不回填旧结果。
        if (Volatile.Read(ref _epoch) == epoch)
        {
            ResolvedMaps[type] = new ResolvedMap(epoch, accessor);
        }

        return accessor is not null;
    }

    /// <summary>沿基类链找访问器：生成器按声明类型注册，运行时是派生实例也能命中。</summary>
    private static MapAccessor? ResolveAccessor(Type type)
    {
        // 生成器按声明类型注册，沿基类链命中。
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (ShapeRegistry.TryGetMap(current, out var accessor))
            {
                return accessor;
            }
        }

        // 只查接口（含基类继承下来的）。
        foreach (var contract in EnumerateSelfAndInterfaces(type))
        {
            if (contract != type && ShapeRegistry.TryGetMap(contract, out var accessor))
            {
                return accessor;
            }
        }

        return null;
    }

    /// <summary>注册表新增映射时推进纪元并丢掉按类型缓存的结果。</summary>
    public static void Invalidate()
    {
        Interlocked.Increment(ref _epoch);
        ResolvedMaps.Clear();
    }

    /// <summary>按键值对枚举字典；键统一转成字符串，值保持原始对象（由调用方决定怎么用）。</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Enumerate(object dictionary)
    {
        switch (dictionary)
        {
            case IDictionary<string, object?> objectMap:
                foreach (var entry in objectMap)
                {
                    // null 键跳过，不变成 "" 或空引用。
                    if (entry.Key is not null)
                    {
                        yield return entry;
                    }
                }

                break;

            case IReadOnlyDictionary<string, object?> objectReadOnlyMap:
                foreach (var entry in objectReadOnlyMap)
                {
                    if (entry.Key is not null)
                    {
                        yield return entry;
                    }
                }

                break;

            case IDictionary map:
                foreach (DictionaryEntry entry in map)
                {
                    // ToString() 也可能返回 null，同样跳过。
                    if (entry.Key?.ToString() is { } key)
                    {
                        yield return new KeyValuePair<string, object?>(key, entry.Value);
                    }
                }

                break;

            default:
            {
                var type = dictionary.GetType();
                if (TryGetAccessor(dictionary, out var accessor))
                {
                    foreach (var entry in accessor.Enumerate(dictionary))
                    {
                        if (entry.Key is null)
                        {
                            continue;
                        }

                        yield return new KeyValuePair<string, object?>(entry.Key, entry.Value);
                    }

                    break;
                }

                // 必须判 RuntimeFeature.IsDynamicCodeSupported 本身：ILC 折成常量 false 后反射实现才被裁掉。
                if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
                {
                    // 反射回退：按 KeyValuePair 读取。
                    foreach (var entry in QueryMapReflection.EnumeratePairs(dictionary))
                    {
                        yield return entry;
                    }

                    break;
                }

                throw AxiosAotMetadataException.MissingMap(type);
            }
        }
    }

    /// <summary>在类型自身与接口里找字符串键字典契约；<paramref name="valueType"/> 非 null 时还要求值类型相符。</summary>
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

    /// <summary>枚举类型自身与它的全部接口：字典契约可能声明在接口上，要逐个试。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "字典类型由调用方运行时提供；AOT 下需由消费方 root 类型所在程序集（README「运行时与 AOT」）。")]
    private static IEnumerable<Type> EnumerateSelfAndInterfaces(Type type)
    {
        yield return type;
        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }
    }

    /// <summary>具体类型 → 注册表访问器（<c>null</c> 表示没有）；条目带纪元，注册表更新后自动失效。</summary>
    private static readonly ConcurrentDictionary<Type, ResolvedMap> ResolvedMaps = new();

    // 注册表纪元：每次注册 +1。
    private static int _epoch;

    /// <summary>缓存条目：写入时的注册表纪元 + 访问器（<c>null</c> 表示不是字符串键字典）。</summary>
    /// <param name="Epoch">写入时的注册表纪元。</param>
    /// <param name="Accessor">字典访问器；<c>null</c> 表示不是字符串键字典。</param>
    private readonly record struct ResolvedMap(int Epoch, MapAccessor? Accessor);

    /// <summary>类型是否「字符串键字典」的缓存：纯类型形状，与注册表无关。</summary>
    private static readonly ConcurrentDictionary<Type, bool> MapTypes = new();
}
