using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

// 请求模型的启动期校验：把「首次调用才抛」的模型定义错误提前到进程启动。
public sealed partial class ApiClient
{
    /// <summary>
    /// 校验一组请求模型，返回<b>全部</b>错误消息（空列表 = 全部合法），不抛异常。
    /// <para>
    /// 用于启动期一次性体检：<c>var errors = ApiClient.ValidateModels(typeof(GetUserRequest), ...);</c>
    /// 有错就 <c>throw new InvalidOperationException(string.Join(Environment.NewLine, errors))</c>。
    /// </para>
    /// <para>与首次调用走同一份元数据构建逻辑（结果同样进缓存），因此不会出现「校验通过、调用仍报错」。</para>
    /// </summary>
    /// <param name="requestTypes">待校验的请求模型类型。</param>
    /// <exception cref="ArgumentNullException"><paramref name="requestTypes"/> 为 <c>null</c>。</exception>
    public static IReadOnlyList<string> ValidateModels(params Type[] requestTypes)
    {
        ArgumentNullException.ThrowIfNull(requestTypes);

        return ValidateCore(requestTypes);
    }

    /// <summary>
    /// 扫描程序集里全部 <see cref="IApiRequest{TResponse}"/> 实现并校验，返回全部错误消息。
    /// <para>只检查实现该接口的具体类型；动态加载失败的类型会被跳过（<see cref="ReflectionTypeLoadException"/>）。</para>
    /// </summary>
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
            // 依赖缺失的加载器不会给出完整类型表：能取到的照样校验，剩下的交给运行时。
            types = [.. ex.Types.OfType<Type>()];
        }

        return ValidateCore(types.Where(ImplementsApiRequest));
    }

    // params Type[] 上的裁剪标注经过 IEnumerable<Type> 传递后会丢失，这里按「运行时类型」放宽。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "类型由调用方以 typeof(T) 传入；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
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
                // 构建成功即通过；错误在 Build 里已缓存，这里读到的就是首次调用会看到的那份消息
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
