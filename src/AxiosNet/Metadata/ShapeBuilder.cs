using System.ComponentModel;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// 对象形状构建器（泛型版）：生成代码用 <c>default(T)</c> 或匿名对象样本推断 <typeparamref name="T"/>，
/// 每个成员写一条直接取值委托，运行期展平不再反射。
/// </summary>
/// <typeparam name="T">参与 params / 表单 / multipart 展平的类型（可以是匿名类型）。</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ShapeBuilder<T>
{
    private readonly List<ShapeMemberDraft> _members = [];

    internal ShapeBuilder()
    {
    }

    /// <summary>声明一个成员：字段名与成员名相同、不忽略。</summary>
    public ShapeBuilder<T> Member(string name, Func<T, object?> getValue)
        => Member(name, name, ignored: false, getValue);

    /// <summary>声明一个成员；顺序即展平顺序（生成形状必须与反射形状一致，忽略的成员也要保留位置）。</summary>
    public ShapeBuilder<T> Member(string name, string fieldName, bool ignored, Func<T, object?> getValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(getValue);

        _members.Add(new ShapeMemberDraft(name, fieldName, ignored, getValue));
        return this;
    }

    internal ObjectShape Build(Type type)
    {
        var members = new ShapeMember[_members.Count];
        for (var i = 0; i < members.Length; i++)
        {
            var draft = _members[i];
            members[i] = new ShapeMember(
                draft.Name,
                draft.FieldName,
                draft.Ignored,
                typeof(object),
                target => draft.GetValue((T)target));
        }

        return new ObjectShape(type, members);
    }

    private sealed record ShapeMemberDraft(string Name, string FieldName, bool Ignored, Func<T, object?> GetValue);
}
