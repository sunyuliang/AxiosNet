using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Internal;

/// <summary>
/// 对象形状缓存：params / 表单 / multipart 展平所需的成员清单。
/// 注册表（源生成或手工注册）优先；JIT 下按类型反射构建并缓存；AOT 下缺少形状直接失败，不猜测基类布局。
/// </summary>
internal static class ShapeCache
{
    /// <summary>JIT 反射构建的形状（AOT 从不写入）。</summary>
    private static readonly ConcurrentDictionary<Type, ObjectShape> ReflectionShapes = new();

    /// <summary>
    /// 取类型形状；<paramref name="fieldPath"/> 是展平到当前位置的字段名（只用于 AOT 缺失时的错误定位）。
    /// 形状声明损坏（注册期 configure 抛错）时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static ObjectShape Get(Type type, string? fieldPath = null)
    {
        if (GeneratedMetadataRegistry.TryGetShape(type, out var registered))
        {
            return Validate(registered);
        }

        if (!RuntimeSupport.IsDynamicCodeSupported)
        {
            throw AxiosAotMetadataException.MissingShape(type, fieldPath);
        }

        return Validate(ReflectionShapes.GetOrAdd(type, CreateReflectionShape));
    }

    /// <summary>
    /// 尽力取形状（回收已交出所有权的流 / 内容时使用）：AOT 下没有形状就停止下钻，不抛错。
    /// 注册期损坏的形状同样静默跳过——回收路径不应成为第二个报错点。
    /// </summary>
    public static bool TryGet(Type type, [NotNullWhen(true)] out ObjectShape? shape)
    {
        if (GeneratedMetadataRegistry.TryGetShape(type, out var registered))
        {
            shape = registered.Error is null ? registered : null;
            return shape is not null;
        }

        if (!RuntimeSupport.IsDynamicCodeSupported)
        {
            shape = null;
            return false;
        }

        shape = ReflectionShapes.GetOrAdd(type, CreateReflectionShape);
        return true;
    }

    /// <summary>显式注册压过旧的反射缓存（同一类型的热路径第一次读取前完成）。</summary>
    public static void Invalidate(Type type) => ReflectionShapes.TryRemove(type, out _);

    public static void InvalidateAll() => ReflectionShapes.Clear();

    private static ObjectShape Validate(ObjectShape shape)
        => shape.Error is null ? shape : throw new InvalidOperationException(shape.Error);

    /// <summary>ref struct / 指针：无法装箱成 object，形状里一律跳过（与生成器口径一致）。</summary>
    internal static bool IsUnboxable(Type type)
        => type.IsByRefLike || type.IsPointer
#if NET7_0_OR_GREATER
            || type.IsFunctionPointer
#endif
        ;

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "由 ShapeCache.Get / TryGet 在 JIT 下调用；AOT 下反射分支不可达。")]
    private static ObjectShape CreateReflectionShape(Type type)
    {
        var members = new List<ShapeMember>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // 与生成的形状口径一致：只收录 public 读访问器（非 public getter 两边都跳过）。
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            // ref struct / 指针无法装箱成 object：生成器同样跳过，两边保持一致。
            if (IsUnboxable(property.PropertyType))
            {
                continue;
            }

            var captured = property;
            members.Add(new ShapeMember(
                property.Name,
                ReflectionCache.GetFieldName(property),
                ReflectionCache.IsIgnoredProperty(property),
                property.PropertyType,
                target => PropertyAccessor.GetValue(captured, target)));
        }

        return new ObjectShape(type, [.. members]);
    }
}
