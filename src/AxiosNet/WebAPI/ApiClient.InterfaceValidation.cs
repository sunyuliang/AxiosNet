using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Reflection;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.WebAPI;

public sealed partial class ApiClient
{
    /// <summary>
    /// 校验接口级声明，返回全部错误（空集合 = 合法）；不创建代理，可在启动时扫描后统一报告。
    /// 生成器 / 手工注册过的接口走声明路径（Native AOT 下同样可用）；未注册的接口只在 JIT 下回退反射。
    /// </summary>
    // ILLink 不支持在 Type[] 上标注 DynamicallyAccessedMembers（IL2098），只能在实现处抑制：
    // JIT 回退会反射接口成员，PublishTrimmed 下调用方需自行 root 接口（AOT 下由源生成器提供元数据，不走反射）。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "反射回退只在 JIT 下（动态代码可用时）可达；AOT 下未注册的接口直接报告缺少元数据，不反射。"
            + "PublishTrimmed（非 AOT）下需调用方 root 接口类型。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2062",
        Justification = "同上：反射回退（InterfaceDeclaration.Validate）只在 JIT 下可达，AOT 下提前返回缺少元数据的错误。")]
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

            if (!interfaceType.IsInterface)
            {
                errors.Add($"{interfaceType.FullName} 不是接口：接口级声明只能用于 interface 类型。");
                continue;
            }

            if (interfaceType.IsGenericTypeDefinition)
            {
                errors.Add($"接口 {interfaceType.Name} 是开放泛型：请使用闭合的接口类型（如 IUserApi / IRepo<User>）。");
                continue;
            }

            if (GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry))
            {
                errors.AddRange(entry.ValidateAll());
                continue;
            }

            // 守卫条件必须直接写 intrinsic：ILC 折成常量 false 后，这段反射校验才不可达并被裁掉。
            if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
            {
                errors.AddRange(InterfaceDeclaration.Validate(interfaceType));
                continue;
            }

            errors.Add(AxiosAotMetadataException.MissingInterface(interfaceType).Message);
        }

        return errors;
    }

    /// <summary>扫描程序集中标注了路由特性的接口并校验（普通接口自动跳过）。Native AOT 下不可用，改用生成的 ValidateAll 或按类型校验。</summary>
    /// <exception cref="PlatformNotSupportedException">当前运行时禁用动态代码（Native AOT）：改用生成的 <c>ValidateAll()</c> 或按类型校验。</exception>
    [RequiresUnreferencedCode("程序集扫描通过反射枚举类型；裁剪时需 root 接口程序集。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "程序集扫描本身已标注 RequiresUnreferencedCode：扫描出来的类型无法携带裁剪标注，这里的接口成员访问不再单独报裁剪警告。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2062",
        Justification = "同上：程序集扫描出的类型不具备裁剪标注，接口校验的成员访问由 RequiresUnreferencedCode 统一承担。")]
    public static IReadOnlyList<string> ValidateInterfaces(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureAssemblyScanSupported();

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
