using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

// 请求模型的启动期校验：在进程启动时报告模型定义错误。
public sealed partial class ApiClient
{
    /// <summary>校验一组请求模型，返回全部错误消息（空列表 = 全部合法），不抛异常；与运行时共用同一份元数据构建逻辑。</summary>
    /// <param name="requestTypes">待校验的请求模型类型。</param>
    /// <exception cref="ArgumentNullException"><paramref name="requestTypes"/> 为 <c>null</c>。</exception>
    public static IReadOnlyList<string> ValidateModels(params Type[] requestTypes)
    {
        ArgumentNullException.ThrowIfNull(requestTypes);

        return ValidateCore(requestTypes);
    }

    /// <summary>扫描程序集里全部 <see cref="IApiRequest{TResponse}"/> 实现并校验，返回全部错误消息。只检查可实例化的类型（跳过抽象类 / 接口 / 开放泛型定义）；加载失败的类型会被跳过。</summary>
    /// <param name="assembly">待扫描的程序集。</param>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> 为 <c>null</c>。</exception>
    [RequiresUnreferencedCode(
        "按程序集枚举类型（Assembly.GetTypes）在裁剪后可能漏掉模型类型；AOT 下请改用 ValidateModels(Type[]) 显式传入模型类型。")]
    public static IReadOnlyList<string> ValidateModels(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

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

        return ValidateCore(types.Where(ImplementsApiRequest).Where(IsConcreteInstantiable));
    }

    /// <summary>是否可实例化（程序集扫描的准入判据）：排除抽象类、接口与开放泛型定义。</summary>
    private static bool IsConcreteInstantiable(Type type)
        => !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition;

    // params Type[] 上的裁剪标注经过 IEnumerable<Type> 传递后会丢失，这里按「运行时类型」放宽。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "类型由调用方以 typeof(T) 传入：AOT 下由 AxiosNet 源生成器提供元数据；未注册的类型按 AxiosAotMetadataException 报告，"
            + "不会反射模型成员（README「Native AOT / 裁剪」）。")]
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

            if (!ImplementsApiRequest(type))
            {
                errors.Add($"{Describe(type)}：未实现 IApiRequest<TResponse>，不能作为请求模型。");
                continue;
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

    private static string Describe(Type type) => type.FullName ?? type.Name;
}
