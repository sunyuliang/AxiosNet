using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

public sealed partial class ApiClient
{
    /// <summary>校验接口级声明，返回全部错误（空集合 = 合法）；不创建代理，可在启动时扫描后统一报告。</summary>
    [RequiresUnreferencedCode("接口级校验通过反射读取接口方法与参数特性；裁剪时需 root 接口类型及其参数 / 返回类型。")]
    public static IReadOnlyList<string> ValidateInterfaces(params Type[] interfaceTypes)
    {
        ArgumentNullException.ThrowIfNull(interfaceTypes);

        var errors = new List<string>();
        var seen = new HashSet<Type>();

        foreach (var interfaceType in interfaceTypes)
        {
            if (interfaceType is null)
            {
                errors.Add("ValidateInterfaces 收到 null 类型：请传入具体的接口类型。");
                continue;
            }

            if (!seen.Add(interfaceType))
            {
                continue;
            }

            errors.AddRange(InterfaceDeclaration.Validate(interfaceType));
        }

        return errors;
    }

    /// <summary>扫描程序集中标注了路由特性的接口并校验（普通接口自动跳过）。</summary>
    [RequiresUnreferencedCode("程序集扫描通过反射枚举类型；裁剪时需 root 接口程序集。")]
    public static IReadOnlyList<string> ValidateInterfaces(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var errors = new List<string>();

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 依赖缺失时能取到的类型照样校验。
            types = [.. ex.Types.OfType<Type>()];
        }

        foreach (var type in types)
        {
            if (!type.IsInterface || type.IsGenericTypeDefinition || !InterfaceDeclaration.HasRoute(type))
            {
                continue;
            }

            errors.AddRange(InterfaceDeclaration.Validate(type));
        }

        return errors;
    }
}
