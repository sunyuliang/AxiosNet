using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.Metadata;
using AxiosNet.Reflection;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Reflection;

// 模块初始化器只用来把 WebAPI 的特性解析注册到内核的中性扩展点上（内核不引用 WebAPI）；
// 调用方是任意宿主，这里没有「应用程序代码」可依赖。
#pragma warning disable CA2255

namespace AxiosNet.WebAPI;

/// <summary>
/// 把 <see cref="FormatAttribute"/> 接到对象形状的反射构建上（内核只暴露注册点，不引用本程序集）。
/// 没有本程序集时形状退化为「无成员格式化」——那时特性本身也不存在，语义自洽。
/// </summary>
internal static class FormatRegistration
{
    /// <summary>模块初始化时注册格式化读取器（内核只读注册点）。</summary>
    [ModuleInitializer]
    internal static void Register() => ValueFormatReader.Register(ReadPropertyFormat);

    /// <summary>读属性上的 <c>[Format]</c> 并翻成格式化规格；没有该特性时返回 null。</summary>
    private static ValueFormat? ReadPropertyFormat(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<FormatAttribute>(inherit: true);
        if (attribute is null)
        {
            return null;
        }

        var owner = property.DeclaringType?.Name ?? property.ReflectedType?.Name ?? "类型";
        return FormatSpecReader.Read(attribute, owner, property.Name);
    }
}
