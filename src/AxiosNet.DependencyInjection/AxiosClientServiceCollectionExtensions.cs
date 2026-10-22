using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AxiosNet.DependencyInjection;

/// <summary>
/// 把 <see cref="AxiosClient"/> 以单例形式注册进 .NET 依赖注入容器的扩展方法。
/// </summary>
public static class AxiosClientServiceCollectionExtensions
{
    /// <summary>
    /// 注册单例 <see cref="AxiosClient"/>：连接池、拦截器与实例默认值随容器生命周期共享，
    /// 对应「一个 axios instance 贯穿整个应用」的用法。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">
    /// 对实例默认值（<see cref="AxiosClient.Defaults"/>）的配置（BaseAddress、超时、头、序列化器、
    /// 连接池选项等），null 表示全默认；在一份新建的 <see cref="AxiosConfig"/> 上调用。
    /// </param>
    /// <returns>原 <paramref name="services"/>，支持链式注册。</returns>
    /// <remarks>
    /// TryAdd 语义：容器中已有 <see cref="AxiosClient"/> 注册时本次调用不生效（保留首次注册），
    /// 避免重复创建连接池；要替换请先移除原注册。
    /// </remarks>
    public static IServiceCollection AddAxiosClient(this IServiceCollection services, Action<AxiosConfig>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAdd 语义必须连 configure 的副作用一起跳过：已有注册时「本次调用不生效」是完整的，
        // 不能只丢注册却把 configure 跑一遍（序列化器注册、诊断埋点这类副作用会重复生效）
        if (services.Any(d => d.ServiceType == typeof(AxiosClient)))
        {
            return services;
        }

        var defaults = new AxiosConfig();
        configure?.Invoke(defaults);
        services.TryAdd(ServiceDescriptor.Singleton(typeof(AxiosClient), _ => new AxiosClient(defaults)));
        return services;
    }
}
