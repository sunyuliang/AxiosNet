using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet;

/// <summary>
/// <see cref="AxiosDynamic"/> 的 DLR 绑定：dynamic 成员语法要编译表达式（运行时代码生成），
/// Native AOT 下由 <see cref="IDynamicMetaObjectProvider.GetMetaObject"/> 直接拒绝；索引访问与字典接口不受影响。
/// </summary>
public sealed partial class AxiosDynamic
{
    DynamicMetaObject IDynamicMetaObjectProvider.GetMetaObject(Expression parameter)
    {
        // DLR 需要 Expression.Compile（运行时代码生成）：Native AOT 下直接拒绝，索引 / 字典接口不受影响。
        if (!RuntimeSupport.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                "AxiosDynamic 的 dynamic 成员语法需要运行时代码生成，Native AOT 下不可用："
                + "请改用 data[\"name\"] 或 data.TryGetValue(\"name\", out var value)（索引访问与 IDictionary 接口在 AOT 下可用）。");
        }

        return new Meta(parameter, this);
    }

    /// <summary>DLR 绑定用到的私有成员句柄：放在嵌套类里，只在 dynamic 语法首次绑定（JIT）时初始化。</summary>
    private static class Dlr
    {
        internal static readonly FieldInfo ShapeField =
            typeof(AxiosDynamic).GetField(nameof(_shape), BindingFlags.NonPublic | BindingFlags.Instance)!;

        internal static readonly FieldInfo ValuesField =
            typeof(AxiosDynamic).GetField(nameof(_values), BindingFlags.NonPublic | BindingFlags.Instance)!;

        internal static readonly MethodInfo GetValueMethod =
            typeof(AxiosDynamic).GetMethod(nameof(GetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

        internal static readonly MethodInfo SetValueMethod =
            typeof(AxiosDynamic).GetMethod(nameof(SetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    /// <summary>DLR 绑定：把成员名解析成常量槽位，并用 shape 引用做校验。</summary>
    private sealed class Meta : DynamicMetaObject
    {
        public Meta(Expression expression, AxiosDynamic value)
            : base(expression, BindingRestrictions.Empty, value)
        {
        }

        public override DynamicMetaObject BindGetMember(GetMemberBinder binder)
        {
            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var shape = ((AxiosDynamic)Value!)._shape; // 绑定必然拿到实例本身，Value 不会为 null

            // 名字 → 槽位在绑定期解析成常量；shape 是缓存键的一部分，同形状的实例共享规则。
            var restrictions = BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic))
                .Merge(BindingRestrictions.GetExpressionRestriction(
                    Expression.ReferenceEqual(Expression.Field(self, Dlr.ShapeField), Expression.Constant(shape))));

            Expression body = shape.Slots.TryGetValue(binder.Name, out var slot)
                ? Expression.ArrayIndex(Expression.Field(self, Dlr.ValuesField), Expression.Constant(slot))
                : Expression.Constant(null, typeof(object));

            return new DynamicMetaObject(body, restrictions);
        }

        public override DynamicMetaObject BindSetMember(SetMemberBinder binder, DynamicMetaObject value)
        {
            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var assigned = Expression.Variable(typeof(object), "value");
            var body = Expression.Block(
                new[] { assigned },
                Expression.Assign(assigned, Expression.Convert(value.Expression, typeof(object))),
                Expression.Call(self, Dlr.SetValueMethod, Expression.Constant(binder.Name), assigned),
                assigned);

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }

        public override DynamicMetaObject BindGetIndex(GetIndexBinder binder, DynamicMetaObject[] indexes)
        {
            if (indexes.Length != 1)
            {
                return binder.FallbackGetIndex(this, indexes);
            }

            var body = Expression.Call(Expression.Convert(Expression, typeof(AxiosDynamic)), Dlr.GetValueMethod, Expression.Convert(indexes[0].Expression, typeof(string)));

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }

        public override DynamicMetaObject BindSetIndex(SetIndexBinder binder, DynamicMetaObject[] indexes, DynamicMetaObject value)
        {
            if (indexes.Length != 1)
            {
                return binder.FallbackSetIndex(this, indexes, value);
            }

            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var assigned = Expression.Variable(typeof(object), "value");
            var body = Expression.Block(
                new[] { assigned },
                Expression.Assign(assigned, Expression.Convert(value.Expression, typeof(object))),
                Expression.Call(self, Dlr.SetValueMethod, Expression.Convert(indexes[0].Expression, typeof(string)), assigned),
                assigned);

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }
    }
}
