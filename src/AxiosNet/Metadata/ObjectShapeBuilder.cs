using System.ComponentModel;
using AxiosNet.Internal;

namespace AxiosNet.Metadata;

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

    /// <summary>冻结成不可变形状（生成器与手工注册共用这一条出口）。</summary>
    internal ObjectShape Build()
        => new(_type, [.. _members.Select(static draft => new ShapeMember(
            draft.Name,
            draft.FieldName,
            draft.Ignored,
            draft.ValueType,
            draft.GetValue,
            draft.Format))]);

    /// <summary>成员草稿：<see cref="ObjectShapeMemberBuilder"/> 在构建完成前就地填字段。</summary>
    internal sealed class ShapeMemberDraft
    {
        // 草稿字段：构建期就地可改，Build 时一次性转成不可变形状。
        public string Name { get; init; } = string.Empty;

        public string FieldName { get; set; } = string.Empty;

        public bool Ignored { get; set; }

        public Type ValueType { get; init; } = typeof(object);

        public Func<object, object?> GetValue { get; init; } = static _ => null;

        public ValueFormat? Format { get; set; }
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

    /// <summary>整体忽略（等价于 <c>[JsonIgnore(Condition = JsonIgnoreCondition.Always)]</c>）。</summary>
    public ObjectShapeBuilder Ignore()
    {
        _draft.Ignored = true;
        return _owner;
    }

    /// <summary>成员级格式化（等价于 <c>[Format]</c>）：只影响库转文本的通道。</summary>
    /// <param name="format">格式化；<c>null</c> 表示走通道默认规则。</param>
    public ObjectShapeBuilder Format(ValueFormat? format)
    {
        _draft.Format = format;
        return _owner;
    }
}
