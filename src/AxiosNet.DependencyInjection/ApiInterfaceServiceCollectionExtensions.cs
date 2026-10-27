using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// 把接口级声明注册进依赖注入容器：解析时用容器里的 <see cref="ApiClient"/> 创建代理，并以单例复用
/// （代理创建依赖 DispatchProxy 生成类型，不值得每次解析都重建）。
/// </summary>
public static class ApiInterfaceServiceCollectionExtensions
{
    /// <summary>为接口注册单例代理，使用未命名的 <see cref="ApiClient"/>（需先调用 <c>AddApiClient(...)</c>）。</summary>
    [RequiresUnreferencedCode("接口级声明通过反射读取接口方法与参数特性；裁剪时需 root 接口类型及其参数 / 返回类型。")]
    public static IServiceCollection AddApiInterface<TInterface>(this IServiceCollection services)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureInterfaceType<TInterface>();

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
    [RequiresUnreferencedCode("接口级声明通过反射读取接口方法与参数特性；裁剪时需 root 接口类型及其参数 / 返回类型。")]
    public static IServiceCollection AddApiInterface<TInterface>(this IServiceCollection services, string name)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureInterfaceType<TInterface>();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name 不能为空白。", nameof(name));
        }

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

    /// <summary>注册期就拒绝非接口类型：否则错误要等到容器解析时才暴露。</summary>
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
}
