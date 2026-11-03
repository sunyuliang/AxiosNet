using System.ComponentModel;
using AxiosNet.Internal;

namespace AxiosNet.Metadata;

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
        => Member(name, fieldName, ignored, getValue, format: null);

    /// <summary>声明一个成员并带成员级格式化（<c>[Format]</c> 的烘焙形态）。</summary>
    /// <param name="name">成员名。</param>
    /// <param name="fieldName">表单 / multipart 字段名。</param>
    /// <param name="ignored">是否整体忽略（等价于 <c>[JsonIgnore(Always)]</c>）。</param>
    /// <param name="getValue">成员取值委托。</param>
    /// <param name="format">成员级格式化；<c>null</c> 表示走通道默认规则。</param>
    public ShapeBuilder<T> Member(string name, string fieldName, bool ignored, Func<T, object?> getValue, ValueFormat? format)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(getValue);

        _members.Add(new ShapeMemberDraft(name, fieldName, ignored, getValue, format));
        return this;
    }

    /// <summary>泛型构建器的出口：草稿转成已绑定取值委托的形状。</summary>
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
                target => draft.GetValue((T)target),
                draft.Format);
        }

        return new ObjectShape(type, members);
    }

    /// <summary>展平过程中的中间记录：一个成员的名字、字段名、忽略标记、取值委托与格式化钩子。</summary>
    /// <param name="Name">CLR 成员名。</param>
    /// <param name="FieldName">展平后使用的字段名（特性改名后的名字）。</param>
    /// <param name="Ignored">是否被忽略，展平时跳过。</param>
    /// <param name="GetValue">直接取值的委托，避免运行期反射 <c>PropertyInfo</c>。</param>
    /// <param name="Format">成员级格式化钩子；null 表示原样输出。</param>
    private sealed record ShapeMemberDraft(string Name, string FieldName, bool Ignored, Func<T, object?> GetValue, ValueFormat? Format);
}
