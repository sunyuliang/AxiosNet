using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Reflection;

/// <summary>
/// 把 <see cref="FormatAttribute"/> 解析成运行期可用的 <see cref="ValueFormat"/>（只在 JIT 反射路径调用；
/// AOT 下由源生成器把同一个委托直接写进元数据）。
/// </summary>
internal static class FormatSpecReader
{
    /// <summary>解析声明上的格式化；函数签名不合法时抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="attribute">特性实例。</param>
    /// <param name="owner">定位前缀（如 <c>请求模型 UserRequest</c>）。</param>
    /// <param name="member">成员名。</param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "反射体在「支持动态代码」的正体里；AOT 下由源生成器把格式化委托直接写进元数据，本方法只会走守卫分支。")]
    public static ValueFormat Read(FormatAttribute attribute, string owner, string member)
    {
        if (attribute.Format is { } format)
        {
            return ValueFormat.FromFormat(format);
        }

        var type = attribute.FormatterType!;
        var methodName = attribute.FormatterMethod!;

        // AOT 下手动注册的形状没有生成期烘焙：反射取静态方法拿不到方法体，宁可给明确指引也不留谜语。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            var method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(object)],
                modifiers: null);

            if (method is null)
            {
                throw new InvalidOperationException(
                    $"{owner} 的成员 {member} 标注了 [Format(typeof({type.Name}), \"{methodName}\")]，"
                    + $"但 {type.Name} 没有可用的格式化函数：签名必须是 public static string? {methodName}(object? value)。");
            }

            if (method.ReturnType != typeof(string))
            {
                throw new InvalidOperationException(
                    $"{owner} 的成员 {member} 的格式化函数 {type.Name}.{methodName} 返回 {method.ReturnType.Name}："
                    + "签名必须是 public static string? M(object? value)（返回 null 表示回落默认规则）。");
            }

            return ValueFormat.FromDelegate(method.CreateDelegate<Func<object?, string?>>());
        }

        throw new InvalidOperationException(
            $"{owner} 的成员 {member} 的 [Format] 指向格式化函数 {type.Name}.{methodName}，"
            + "但 Native AOT 下无法用反射取静态方法：请启用 AxiosNet 源生成器（生成期把委托烘焙进元数据），"
            + "或改用 [Format(\"...\")] 格式串。");
    }
}
