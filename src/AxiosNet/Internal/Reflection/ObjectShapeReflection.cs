using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Internal.Reflection;

/// <summary>
/// 反射版对象形状：按公共可读属性构建（缓存归 <see cref="ShapeCache"/>，本类只负责构建）。
/// 与生成形状的口径必须逐项一致：只收 public 读访问器、跳过索引器与无法装箱的类型。
/// </summary>
internal static class ObjectShapeReflection
{
    /// <summary>按公共可读属性构造形状（<c>AxiosMetadata.RegisterShape&lt;T&gt;()</c> 的兜底实现）。</summary>
    internal static ObjectShape FromReflection<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        => Create(typeof(T));

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "只由 ShapeCache 的 JIT 分支调用，或由 RegisterShape<T> 的 DynamicallyAccessedMembers 标注覆盖；AOT 下反射分支不可达。")]
    internal static ObjectShape Create(Type type)
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

    /// <summary>ref struct / 指针：无法装箱成 object，形状里一律跳过（与生成器口径一致）。</summary>
    private static bool IsUnboxable(Type type)
        => type.IsByRefLike || type.IsPointer
#if NET7_0_OR_GREATER
            || type.IsFunctionPointer
#endif
        ;
}
