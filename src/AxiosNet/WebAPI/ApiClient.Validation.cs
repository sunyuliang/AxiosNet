using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.WebAPI;

// 请求模型的启动期校验：在进程启动时报告模型定义错误。
public sealed partial class ApiClient
{
    /// <summary>
    /// 校验一组请求模型，返回全部错误消息（空列表 = 全部合法），不抛异常；与运行时共用同一份元数据构建逻辑。
    /// 生成器 / 手工注册过的模型走声明路径（Native AOT 下同样可用）；未注册的模型只在 JIT 下回退反射。
    /// </summary>
    /// <param name="requestTypes">待校验的请求模型类型。</param>
    /// <exception cref="ArgumentNullException"><paramref name="requestTypes"/> 为 <c>null</c>。</exception>
    public static IReadOnlyList<string> ValidateModels(params Type[] requestTypes)
    {
        ArgumentNullException.ThrowIfNull(requestTypes);

        return ValidateCore(requestTypes);
    }

    /// <summary>扫描程序集里全部 <see cref="IApiRequest{TResponse}"/> 实现并校验，返回全部错误消息。只检查可实例化的类型（跳过抽象类 / 接口 / 开放泛型定义）；加载失败的类型会被跳过。Native AOT 下不可用，改用生成的 ValidateAll 或按类型校验。</summary>
    /// <param name="assembly">待扫描的程序集。</param>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> 为 <c>null</c>。</exception>
    /// <exception cref="PlatformNotSupportedException">当前运行时禁用动态代码（Native AOT）：改用生成的 <c>ValidateAll()</c> 或按类型校验。</exception>
    [RequiresUnreferencedCode(
        "按程序集枚举类型（Assembly.GetTypes）在裁剪后可能漏掉模型类型；AOT 下请改用 ValidateModels(Type[]) 显式传入模型类型。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "程序集扫描本身已标注 RequiresUnreferencedCode：裁剪后可能漏类型，扫描出来的类型也无法携带裁剪标注，这里的成员访问不再单独报裁剪警告。")]
    public static IReadOnlyList<string> ValidateModels(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureAssemblyScanSupported();

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 依赖缺失时取到的类型照样校验，其余交给运行时。
            types = [.. ex.Types.OfType<Type>()];
        }

        return ValidateCore(types.Where(IsConcreteInstantiable).Where(ImplementsApiRequest));
    }

    /// <summary>
    /// 程序集扫描（<c>Assembly.GetTypes</c>）在 Native AOT / 裁剪下无法保证看到全部类型：
    /// 直接失败并指向上限更明确的替代入口，而不是悄悄少校验几个类型。
    /// </summary>
    private static void EnsureAssemblyScanSupported()
    {
        if (RuntimeSupport.IsDynamicCodeSupported)
        {
            return;
        }

        throw new PlatformNotSupportedException(
            "程序集扫描（ValidateModels(Assembly) / ValidateInterfaces(Assembly)）依赖反射枚举类型，Native AOT 下不可用。"
            + "请改用生成的 AxiosNet.Generated.AxiosNetValidation_<程序集名>.ValidateAll()（覆盖全部已生成元数据的模型与接口），"
            + "或按类型调用 ValidateModels(Type[]) / ValidateInterfaces(Type[])。");
    }

    /// <summary>是否可实例化（程序集扫描的准入判据）：排除抽象类、接口与开放泛型定义。</summary>
    private static bool IsConcreteInstantiable(Type type)
        => !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition;

    // ILLink 不支持在 Type[] / IEnumerable<Type> 上标注 DynamicallyAccessedMembers（IL2098），只能在实现处抑制：
    // JIT 回退会反射模型成员，PublishTrimmed 下调用方需自行 root 模型（AOT 下由源生成器提供元数据，不走反射）。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "类型由调用方以 typeof(T) 传入：AOT 下由 AxiosNet 源生成器提供元数据；未注册的类型按 AxiosAotMetadataException 报告，"
            + "不会反射模型成员（README「Native AOT / 裁剪」）。PublishTrimmed（非 AOT）下需调用方 root 模型类型。")]
    private static IReadOnlyList<string> ValidateCore(IEnumerable<Type> types)
    {
        var errors = new List<string>();

        foreach (var type in types)
        {
            if (type is null)
            {
                errors.Add("请求模型类型为 null。");
                continue;
            }

            if (type.IsAbstract || type.IsInterface)
            {
                errors.Add($"{Describe(type)}：请求模型必须是可实例化的具体类。");
                continue;
            }

            // 已注册声明由调用方显式给出，直接按声明构建（与 P0 起的运行时路径一致）；
            // 未注册的类型：AOT 下无法反射，报缺元数据；JIT 下回退反射判定。
            if (GeneratedMetadataRegistry.TryGetRequestModel(type, out var declaration))
            {
                // 判据不能只看「有没有声明」：生成器 / 手工注册只烘焙事实，是否实现 IApiRequest<TResponse>
                // 仍以此标志为准，否则 AOT 声明路径会放过 JIT 反射路径会拒绝的类型。
                if (!declaration.ImplementsApiRequest)
                {
                    errors.Add(NotARequestModel(type));
                    continue;
                }
            }
            else
            {
                if (!RuntimeSupport.IsDynamicCodeSupported)
                {
                    errors.Add(AxiosAotMetadataException.MissingRequestModel(type).Message);
                    continue;
                }

                if (!ImplementsApiRequest(type))
                {
                    errors.Add(NotARequestModel(type));
                    continue;
                }
            }

            try
            {
                // 构建成功即通过；错误消息已在 Build 里缓存
                RequestModelMetadata.Get(type);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{Describe(type)}：{ex.Message}");
            }
        }

        return errors;
    }

    private static bool ImplementsApiRequest(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
        => type.GetInterfaces().Any(candidate
            => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IApiRequest<>));

    /// <summary>「不是请求模型」的统一措辞：声明路径与反射路径共用，避免两处文案漂移。</summary>
    private static string NotARequestModel(Type type)
        => $"{Describe(type)}：未实现 IApiRequest<TResponse>，不能作为请求模型。";

    private static string Describe(Type type) => type.FullName ?? type.Name;
}
