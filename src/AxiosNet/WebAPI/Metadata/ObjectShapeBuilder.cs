using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>对象形状构建器：params / 表单 / multipart 展平使用的成员清单（生成期烘焙或手工注册）。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ObjectShapeBuilder
{
    private readonly Type _type;
    private readonly List<ShapeMemberDraft> _members = [];

    internal ObjectShapeBuilder(Type type)
    {
        _type = type;
    }

    /// <summary>声明一个成员；顺序即展平顺序（生成形状必须与反射形状一致）。</summary>
    public ObjectShapeMemberBuilder Member(string name, Type valueType, Func<object, object?> getValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(getValue);

        var draft = new ShapeMemberDraft
        {
            Name = name,
            FieldName = name,
            ValueType = valueType,
            GetValue = getValue
        };

        _members.Add(draft);
        return new ObjectShapeMemberBuilder(this, draft);
    }

    internal ObjectShape Build()
        => new(_type, [.. _members.Select(static draft => new ShapeMember(
            draft.Name,
            draft.FieldName,
            draft.Ignored,
            draft.ValueType,
            draft.GetValue))]);

    /// <summary>按公共可读属性构造反射形状（<c>RegisterShape&lt;T&gt;()</c> 的兜底实现）。</summary>
    internal static ObjectShape FromReflection<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
    {
        var type = typeof(T);
        var members = new List<ShapeMember>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
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

    internal sealed class ShapeMemberDraft
    {
        public string Name { get; init; } = string.Empty;

        public string FieldName { get; set; } = string.Empty;

        public bool Ignored { get; set; }

        public Type ValueType { get; init; } = typeof(object);

        public Func<object, object?> GetValue { get; init; } = static _ => null;
    }
}

/// <summary>形状成员构建器：字段名 / 忽略标记，都返回所属形状构建器。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ObjectShapeMemberBuilder
{
    private readonly ObjectShapeBuilder _owner;
    private readonly ObjectShapeBuilder.ShapeMemberDraft _draft;

    internal ObjectShapeMemberBuilder(ObjectShapeBuilder owner, ObjectShapeBuilder.ShapeMemberDraft draft)
    {
        _owner = owner;
        _draft = draft;
    }

    /// <summary>表单 / multipart 字段名（默认与成员名相同）。</summary>
    public ObjectShapeBuilder FieldName(string fieldName)
    {
        _draft.FieldName = fieldName;
        return _owner;
    }

    /// <summary>整体忽略（<c>[JsonIgnore(Always)]</c>）。</summary>
    public ObjectShapeBuilder Ignore()
    {
        _draft.Ignored = true;
        return _owner;
    }
}
