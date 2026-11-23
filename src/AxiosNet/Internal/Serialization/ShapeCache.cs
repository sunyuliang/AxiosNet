using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Internal.Serialization;

/// <summary>对象形状入口：params / 表单 / multipart 展平所需的成员清单。</summary>
/// <remarks>注册表（源生成或手工注册）优先；JIT 下回退反射构建并缓存；AOT 下缺少形状直接失败。</remarks>
internal static class ShapeCache
{
    /// <summary>反射形状缓存的目标条目上限（2048）。</summary>
    /// <remarks>键是 <see cref="Type"/>，强引用会阻止可回收 <c>AssemblyLoadContext</c> 卸载。</remarks>
    private const int MaxEntries = 2048;

    /// <summary>触发整体清空的条目数（<see cref="MaxEntries"/> 的 2 倍）。</summary>
    private const int ClearThreshold = MaxEntries * 2;

    /// <summary>反射构建的形状（AOT 下从不写入：填充它的分支被裁掉了）。</summary>
    private static readonly ConcurrentDictionary<Type, ObjectShape> Cache = new();

    /// <summary>取类型形状；<paramref name="fieldPath"/> 只用于 AOT 缺失时的错误定位。</summary>
    /// <exception cref="InvalidOperationException">形状声明损坏（注册期 configure 抛错）。</exception>
    public static ObjectShape Get(Type type, string? fieldPath = null)
    {
        if (ShapeRegistry.TryGetShape(type, out var registered))
        {
            return Validate(registered);
        }

        // 条件里必须是 RuntimeFeature.IsDynamicCodeSupported 本身，ILC 折成常量 false 才裁掉反射实现。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            return Validate(GetOrAdd(type));
        }

        throw AxiosAotMetadataException.MissingShape(type, fieldPath);
    }

    /// <summary>尽力取形状（回收路径用）：AOT 下没有形状或形状损坏时返回 <c>false</c>，不抛错。</summary>
    public static bool TryGet(Type type, [NotNullWhen(true)] out ObjectShape? shape)
    {
        if (ShapeRegistry.TryGetShape(type, out var registered))
        {
            shape = registered.Error is null ? registered : null;
            return shape is not null;
        }

        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            shape = GetOrAdd(type);
            return true;
        }

        shape = null;
        return false;
    }

    /// <summary>显式注册压过旧的反射缓存。</summary>
    public static void Invalidate(Type type) => Cache.TryRemove(type, out _);

    /// <summary>清空反射形状缓存。</summary>
    public static void InvalidateAll() => Cache.Clear();

    /// <summary>取类型形状，没有就用反射建一份并缓存。</summary>
    private static ObjectShape GetOrAdd(Type type)
    {
        if (Cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var shape = ObjectShapeReflection.Create(type);

        // Count 会取全部锁，只在未命中时调。
        if (Cache.Count >= ClearThreshold)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(type, shape);
    }

    /// <summary>构建失败（<c>Error</c> 非 null）就抛出。</summary>
    private static ObjectShape Validate(ObjectShape shape)
        => shape.Error is null ? shape : throw new InvalidOperationException(shape.Error);
}
