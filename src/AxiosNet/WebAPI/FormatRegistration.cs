using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.Metadata;
using AxiosNet.Reflection;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Reflection;

// 库内的模块初始化器只用来把声明式特性接到核心的可空回调上（核心不能反向引用 WebAPI）；
// 调用方是任意宿主，这里没有「应用程序代码」可依赖。
#pragma warning disable CA2255

namespace AxiosNet.WebAPI;

/// <summary>
/// 把 <see cref="FormatAttribute"/> 接到对象形状的反射构建上（核心只暴露可空回调，不引用本目录）。
/// 删掉 <c>WebAPI/</c> 后本文件一并消失，形状退化为「无成员格式化」——那时特性本身也不存在，语义自洽。
/// </summary>
internal static class FormatRegistration
{
    [ModuleInitializer]
    internal static void Register() => ShapeFormatReader.Reader = ReadPropertyFormat;

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
