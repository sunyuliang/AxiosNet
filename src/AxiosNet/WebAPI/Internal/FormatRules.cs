using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AxiosNet.Internal;
using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// <c>[Format]</c> 的构建期校验：值与格式串的形态必须在声明构建期就对得上，不能留到运行期静默回落。
/// 本校验只服务于声明式 API（<c>[Format]</c> 属于 WebAPI 扩展），不参与 Axios 核心的请求发送。
/// </summary>
internal static class FormatRules
{
    /// <summary>校验成员上的格式化声明是否适用于该值的形态；不适用时抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="format">成员级格式化；<c>null</c> 表示未标注，直接放行。</param>
    /// <param name="valueType">成员的声明类型。</param>
    /// <param name="owner">定位前缀（如 <c>请求模型 UserRequest</c>）。</param>
    /// <param name="member">成员名。</param>
    /// <param name="target">绑定目标的可读名称（<c>Path</c> / <c>Query</c> 等），只用于错误消息。</param>
    public static void EnsureApplicable(ValueFormat? format, Type valueType, string owner, string member, string target)
    {
        if (format is null)
        {
            return;
        }

        // 判定要沿接口找 IEnumerable<T>：AOT 下接口元数据可能被裁掉，判不出来会把集合误报成「结构化对象」。
        // 这里宁可少一层构建期校验（运行期行为与未引入本校验前一致），也不能让正确声明跑不起来。
        if (RuntimeSupport.ForceNoDynamicCode || !RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var type = Nullable.GetUnderlyingType(valueType) ?? valueType;

        // 字典由展平通道逐值处理，值类型在运行期才知道：构建期不猜。
        if (QueryMap.IsMapType(type))
        {
            return;
        }

        if (TryGetElementType(type, out var elementType))
        {
            EnsureScalar(format, elementType, owner, member, target, "集合元素");
            return;
        }

        EnsureScalar(format, type, owner, member, target, "值");
    }

    /// <summary>单个值（或集合元素）的形态校验：格式串要求实现 <see cref="IFormattable"/>，函数只要求是标量 / 二进制。</summary>
    private static void EnsureScalar(ValueFormat format, Type type, string owner, string member, string target, string what)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;

        if (ScalarTypes.IsBinaryType(actual))
        {
            if (!format.HasDelegate)
            {
                throw new InvalidOperationException(
                    $"{owner} 的成员 {member} 标注了 [Format(\"...\")]，但{what}类型是 {actual.Name}："
                    + "二进制没有格式串语义，请改用 [Format(typeof(...), nameof(...))] 提供格式化函数。");
            }

            return;
        }

        if (!ScalarTypes.IsScalarType(actual))
        {
            throw new InvalidOperationException(
                $"{owner} 的成员 {member} 在 {target} 上标注了 [Format]，但{what}类型 {actual.Name} 不是标量："
                + "[Format] 只处理标量（集合 / 字典逐元素、逐值应用）；结构化对象请交给序列化器。");
        }

        if (!format.HasDelegate && !typeof(IFormattable).IsAssignableFrom(actual))
        {
            throw new InvalidOperationException(
                $"{owner} 的成员 {member} 标注了 [Format(\"...\")]，但{what}类型 {actual.Name} 没有实现 IFormattable："
                + "格式串无处可用，请改用 [Format(typeof(...), nameof(...))] 提供格式化函数。");
        }
    }

    /// <summary>取集合 / 数组的元素类型（不含 string 与 byte[]，它们按标量处理）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "正体校验只在支持动态代码时调用；<c>[Part]</c> 的文件部件校验在 AOT 下也只依赖已保留的接口元数据，判不出来就放行。")]
    internal static bool TryGetElementType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        elementType = null;

        if (type == typeof(string) || type == typeof(byte[]))
        {
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = contract.GetGenericArguments()[0];
                return true;
            }
        }

        return false;
    }
}
