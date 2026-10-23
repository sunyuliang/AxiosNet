using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AxiosNet.Internal;

/// <summary>
/// 反射元数据缓存：params 序列化与 multipart 展平都在请求热路径上，
/// 每请求调用 <see cref="Type.GetProperties()"/> 会产生可观的分配。
/// 按类型缓存「可读、非索引器」的属性数组，首次访问后零反射开销。
/// </summary>
internal static class ReflectionCache
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

    /// <param name="type">待展平的类型；标注要求裁剪器保留其公开属性（含 getter）。</param>
    public static PropertyInfo[] GetReadableProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        if (Cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        // 用带标注的 type 直接反射；缓存字典的键本身不携带裁剪标注，不能把反射放进工厂 lambda。
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        return Cache.GetOrAdd(type, properties);
    }
}
