using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

public sealed partial class ApiClient
{
    /// <summary>
    /// 为接口创建代理实现（如 <c>var api = client.Create&lt;IUserApi&gt;();</c>）。
    /// 接口方法的绑定语义与请求模型完全一致；代理持有当前 <see cref="ApiClient"/>，请按接口持有一份（DI 注册即单例）。
    /// </summary>
    /// <remarks>
    /// 依赖 DispatchProxy（Reflection.Emit）：仅 JIT 可用，Native AOT 下抛 <see cref="PlatformNotSupportedException"/>。
    /// 接口必须为 public，且每个方法必须返回 <c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>。
    /// </remarks>
    [RequiresUnreferencedCode("接口级声明通过反射读取接口方法、参数特性与类型；裁剪时需 root 接口及其参数 / 返回类型。")]
    public T Create<T>() where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException($"类型 {typeof(T).FullName} 不是接口：Create<T>() 只能用于 interface 类型。", nameof(T));
        }

        EnsureDynamicCodeSupported();
        InterfaceDeclaration.EnsureValid(typeof(T));
        return ApiInterfaceProxy.Create<T>(this);
    }

    /// <summary>非泛型重载（供 DI 等运行时场景使用）；<paramref name="interfaceType"/> 必须是已声明路由的 public 接口。</summary>
    [RequiresUnreferencedCode("接口级声明通过反射读取接口方法、参数特性与类型；裁剪时需 root 接口及其参数 / 返回类型。")]
    public object Create(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] Type interfaceType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!interfaceType.IsInterface)
        {
            throw new ArgumentException($"类型 {interfaceType.FullName} 不是接口：Create(Type) 只能用于 interface 类型。", nameof(interfaceType));
        }

        EnsureDynamicCodeSupported();
        InterfaceDeclaration.EnsureValid(interfaceType);
        return ApiInterfaceProxy.Create(interfaceType, this);
    }

    private static void EnsureDynamicCodeSupported()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException(
                "接口级声明依赖 DispatchProxy（Reflection.Emit），Native AOT 下不可用；"
                + "请继续使用请求模型（IApiRequest<T> + client.RequestAsync(...)），或等待 Source Generator。");
        }
    }
}
