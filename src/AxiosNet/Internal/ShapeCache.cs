using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Internal;

/// <summary>
/// 对象形状入口：params / 表单 / multipart 展平所需的成员清单。
/// 注册表（源生成或手工注册）优先；JIT 下回退反射构建并缓存；AOT 下缺少形状直接失败，不猜测基类布局。
/// </summary>
internal static class ShapeCache
{
    /// <summary>反射构建的形状（AOT 下从不写入：填充它的分支被裁掉了）。</summary>
    private static readonly ConcurrentDictionary<Type, ObjectShape> Cache = new();

    /// <summary>
    /// 取类型形状；<paramref name="fieldPath"/> 是展平到当前位置的字段名（只用于 AOT 缺失时的错误定位）。
    /// 形状声明损坏（注册期 configure 抛错）时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static ObjectShape Get(Type type, string? fieldPath = null)
    {
        if (ShapeRegistry.TryGetShape(type, out var registered))
        {
            return Validate(registered);
        }

        // 条件里必须是 RuntimeFeature.IsDynamicCodeSupported 本身：ILC 折成常量 false 后，这段反射实现才不可达并被裁掉。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            return Validate(GetOrAdd(type));
        }

        throw AxiosAotMetadataException.MissingShape(type, fieldPath);
    }

    /// <summary>
    /// 尽力取形状（回收已交出所有权的流 / 内容时使用）：AOT 下没有形状就停止下钻，不抛错。
    /// 注册期损坏的形状同样静默跳过——回收路径不应成为第二个报错点。
    /// </summary>
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

    /// <summary>显式注册压过旧的反射缓存（同一类型的热路径第一次读取前完成）。</summary>
    public static void Invalidate(Type type) => Cache.TryRemove(type, out _);

    public static void InvalidateAll() => Cache.Clear();

    private static ObjectShape GetOrAdd(Type type) => Cache.GetOrAdd(type, static t => ObjectShapeReflection.Create(t));

    private static ObjectShape Validate(ObjectShape shape)
        => shape.Error is null ? shape : throw new InvalidOperationException(shape.Error);
}
