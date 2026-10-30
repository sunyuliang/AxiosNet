using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// 把接口级声明注册进依赖注入容器：解析时用容器里的 <see cref="ApiClient"/> 创建代理，并以单例复用。
/// </summary>
public static class ApiInterfaceServiceCollectionExtensions
{
    /// <summary>为接口注册单例代理，使用未命名的 <see cref="ApiClient"/>（需先调用 <c>AddApiClient(...)</c>）。</summary>
    /// <remarks>代理来自源生成器 / 手工注册的元数据；未注册时 JIT 回退 DispatchProxy，Native AOT 下在解析时显式失败。</remarks>
    public static IServiceCollection AddApiInterface<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] TInterface>(
        this IServiceCollection services)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureInterfaceType<TInterface>();
        BindInterface<TInterface>(services, name: null);

        services.TryAddSingleton<TInterface>(sp =>
        {
            var client = sp.GetService<ApiClient>()
                ?? throw new InvalidOperationException(
                    $"AddApiInterface<{typeof(TInterface).Name}>() 需要容器中已注册 ApiClient 单例："
                    + "请先调用 services.AddApiClient(...)，或改用带站点名的 AddApiInterface(name) 重载。");

            return client.Create<TInterface>();
        });

        return services;
    }

    /// <summary>为接口注册单例代理，使用命名站点（需先调用带 <c>name</c> 的 <c>AddApiClient(name, baseAddress, ...)</c>）。</summary>
    public static IServiceCollection AddApiInterface<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] TInterface>(
        this IServiceCollection services,
        string name)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureInterfaceType<TInterface>();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name 不能为空白。", nameof(name));
        }

        BindInterface<TInterface>(services, name);

        services.TryAddSingleton<TInterface>(sp =>
        {
            var resolver = sp.GetService<IApiClientResolver>()
                ?? throw new InvalidOperationException(
                    $"AddApiInterface<{typeof(TInterface).Name}>(\"{name}\") 需要命名站点："
                    + "请先调用 services.AddApiClient(name, baseAddress, ...) 注册对应站点。");

            return resolver.Get(name).Create<TInterface>();
        });

        return services;
    }

    /// <summary>注册期就拒绝非接口类型。</summary>
    private static void EnsureInterfaceType<TInterface>()
        where TInterface : class
    {
        if (!typeof(TInterface).IsInterface)
        {
            throw new ArgumentException(
                $"类型 {typeof(TInterface).FullName} 不是接口：AddApiInterface<T>() 只能用于 interface 类型。",
                nameof(TInterface));
        }
    }

    /// <summary>
    /// 同一接口只能绑一个站点：容器按接口类型解析，绑两个站点时第二条会被 <c>TryAddSingleton</c> 静默丢弃，
    /// 请求实际打到先注册的站点——因此这里在注册期就把冲突报出来。同一站点重复注册保持幂等（保留首个）。
    /// </summary>
    private static void BindInterface<TInterface>(IServiceCollection services, string? name)
        where TInterface : class
    {
        var interfaceType = typeof(TInterface);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ApiInterfaceBinding)
                || descriptor.ImplementationInstance is not ApiInterfaceBinding binding
                || binding.InterfaceType != interfaceType)
            {
                continue;
            }

            if (!string.Equals(binding.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"接口 {interfaceType.FullName} 已注册到{DescribeSite(binding.Name)}，不能再注册到{DescribeSite(name)}："
                    + "容器按接口类型解析，同一接口只能对应一个站点。"
                    + "同一站点上的多个接口请分别注册；要在两个站点上使用同一组方法，请定义派生接口"
                    + "（如 interface IOrdersApiForB : IOrdersApi，继承来的方法同样会被代理）再注册到另一个站点。");
            }

            return;
        }

        services.AddSingleton(new ApiInterfaceBinding(interfaceType, name));
    }

    private static string DescribeSite(string? name)
        => name is null ? "未命名站点（AddApiClient(baseAddress)）" : $"站点 \"{name}\"";

    /// <summary>注册期标记：记录接口与站点的绑定关系，供 <see cref="BindInterface{TInterface}"/> 检出冲突。</summary>
    private sealed class ApiInterfaceBinding
    {
        public ApiInterfaceBinding(Type interfaceType, string? name)
        {
            InterfaceType = interfaceType;
            Name = name;
        }

        public Type InterfaceType { get; }

        /// <summary><c>null</c> 表示未命名站点。</summary>
        public string? Name { get; }
    }
}
