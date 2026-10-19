using System.Collections.Concurrent;
using System.Reflection;

namespace AxiosNet.Internal;

/// <summary>
/// 反射元数据缓存：params 序列化与 multipart 展平都在请求热路径上，
/// 每请求调用 <see cref="Type.GetProperties()"/> 会产生可观的分配。
/// 这里按类型缓存「可读、非索引器」的属性数组，首次访问后零反射开销。
/// </summary>
internal static class ReflectionCache
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

    public static PropertyInfo[] GetReadableProperties(Type type)
        => Cache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray());
}
