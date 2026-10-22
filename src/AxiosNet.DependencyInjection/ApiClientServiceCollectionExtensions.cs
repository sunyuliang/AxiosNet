using System.Linq;
using AxiosNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// 把 <see cref="ApiClient"/> 以单例形式注册进 .NET 依赖注入容器的扩展方法，三种模式：
/// 带 <c>baseAddress</c> 的自建模式（底层客户端由本扩展持有）、不带参数的门面模式
/// （复用容器中已注册的 <see cref="AxiosClient"/> 单例），以及带 <c>name</c> 的命名模式
/// （多站点场景，经 <see cref="IApiClientResolver"/> 按站点名取用）。
/// </summary>
public static class ApiClientServiceCollectionExtensions
{
    /// <summary>
    /// 以单例形式注册 <see cref="ApiClient"/>，并自建底层 <see cref="AxiosClient"/>：
    /// BaseAddress 取 <paramref name="baseAddress"/>，其余默认值经 <paramref name="configureDefaults"/> 配置。
    /// 自建客户端与容器里的 <see cref="AxiosClient"/> 注册互不相干（要共享请改用门面模式重载）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="baseAddress">API 基地址（合并规则与 <see cref="AxiosConfig.BaseAddress"/> 一致）。</param>
    /// <param name="configureDefaults">底层客户端实例默认值（超时、头、序列化器、连接池选项等）。</param>
    /// <param name="configure">对 <see cref="ApiClient"/> 自身的配置（典型用法是设置 <see cref="ApiClient.Authorization"/>）。</param>
    /// <returns>原 <paramref name="services"/>，支持链式注册。</returns>
    /// <remarks>
    /// TryAdd 语义：已注册过 <see cref="ApiClient"/> 时本次调用不生效（保留首次注册）。
    /// 容器中注册了 <see cref="IAuthorizationProvider"/> 单例且未显式设置
    /// <see cref="ApiClient.Authorization"/> 时，自动采用它作为授权提供方。
    /// </remarks>
    public static IServiceCollection AddApiClient(
        this IServiceCollection services,
        string baseAddress,
        Action<AxiosConfig>? configureDefaults = null,
        Action<ApiClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new ArgumentException("baseAddress 不能为空白。", nameof(baseAddress));
        }

        // 与 AddAxiosClient 一致：已有 ApiClient 注册时整体不生效（含 configureDefaults 的副作用）
        if (services.Any(d => d.ServiceType == typeof(ApiClient)))
        {
            return services;
        }

        var defaults = new AxiosConfig { BaseAddress = baseAddress };
        configureDefaults?.Invoke(defaults);

        services.TryAdd(ServiceDescriptor.Singleton(typeof(ApiClientOwnedAxiosClient), _ => new ApiClientOwnedAxiosClient(defaults)));
        services.TryAdd(ServiceDescriptor.Singleton(
            typeof(ApiClient),
            sp => ApiClientFactory.Create(sp, sp.GetRequiredService<ApiClientOwnedAxiosClient>().Client, configure)));
        return services;
    }

    /// <summary>
    /// 以单例形式注册一个命名站点（多站点场景）：站点自带 BaseAddress 与底层 <see cref="AxiosClient"/>，
    /// 经 <see cref="IApiClientResolver"/> 按站点名取用——同时对接 a / b / c 多个站点时每个站点一次调用。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="name">站点名（区分大小写，同一容器内必须唯一）。</param>
    /// <param name="baseAddress">该站点的 API 基地址（合并规则与 <see cref="AxiosConfig.BaseAddress"/> 一致）。</param>
    /// <param name="configureDefaults">该站点底层客户端的实例默认值（超时、头、序列化器、连接池选项等）。</param>
    /// <param name="configure">对该站点 <see cref="ApiClient"/> 自身的配置（典型用法是设置 <see cref="ApiClient.Authorization"/>）。</param>
    /// <returns>原 <paramref name="services"/>，支持链式注册。</returns>
    /// <remarks>
    /// 命名站点只经 <see cref="IApiClientResolver"/> 暴露，不注册未命名的 <see cref="ApiClient"/>
    /// （单站点场景请用不带 name 的重载）。站点名重复时注册期即抛 <see cref="InvalidOperationException"/>。
    /// 其余语义（自建底层客户端、容器 <see cref="IAuthorizationProvider"/> 自动采用）与不带 name 的重载一致。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> 或 <paramref name="baseAddress"/> 为空白。</exception>
    /// <exception cref="InvalidOperationException">站点名已注册。</exception>
    public static IServiceCollection AddApiClient(
        this IServiceCollection services,
        string name,
        string baseAddress,
        Action<AxiosConfig>? configureDefaults = null,
        Action<ApiClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name 不能为空白。", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new ArgumentException("baseAddress 不能为空白。", nameof(baseAddress));
        }

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ApiClientDefinition)
                && descriptor.ImplementationInstance is ApiClientDefinition existing
                && string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"站点名 \"{name}\" 已注册：AddApiClient(name, ...) 的站点名必须唯一（区分大小写）。");
            }
        }

        var defaults = new AxiosConfig { BaseAddress = baseAddress };
        configureDefaults?.Invoke(defaults);

        // 解析器是各站点客户端的唯一出口（TryAdd：多个站点共用同一个解析器单例）
        services.TryAddSingleton<IApiClientResolver, ApiClientResolver>();
        services.AddSingleton(new ApiClientDefinition(name, defaults, configure));
        return services;
    }

    /// <summary>
    /// 以单例形式注册 <see cref="ApiClient"/>，包装容器中已注册的 <see cref="AxiosClient"/> 单例
    /// （连接池、拦截器、实例默认值全部共享，需先
    /// <see cref="AxiosClientServiceCollectionExtensions.AddAxiosClient"/>）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">对 <see cref="ApiClient"/> 自身的配置（典型用法是设置 <see cref="ApiClient.Authorization"/>）。</param>
    /// <returns>原 <paramref name="services"/>，支持链式注册。</returns>
    /// <remarks>
    /// 容器中没有 <see cref="AxiosClient"/> 注册时，解析 <see cref="ApiClient"/> 抛
    /// <see cref="InvalidOperationException"/>（请先注册，或改用带 <c>baseAddress</c> 的重载自建）。
    /// 其余语义（TryAdd、<see cref="IAuthorizationProvider"/> 自动采用）与自建模式重载一致。
    /// </remarks>
    public static IServiceCollection AddApiClient(this IServiceCollection services, Action<ApiClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(ServiceDescriptor.Singleton(typeof(ApiClient), sp =>
        {
            var axiosClient = sp.GetService<AxiosClient>();
            if (axiosClient is null)
            {
                throw new InvalidOperationException(
                    "AddApiClient(configure) 需要容器中已注册 AxiosClient 单例："
                    + "请先调用 services.AddAxiosClient(...)，或改用带 baseAddress 的 AddApiClient(baseAddress, ...) 重载。");
            }

            return ApiClientFactory.Create(sp, axiosClient, configure);
        }));
        return services;
    }
}
