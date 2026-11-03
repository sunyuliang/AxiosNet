using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.Simple.DependencyInjection;

/// <summary>场景 1 的消费方：构造函数注入 <see cref="AxiosClient"/> 单例。</summary>
sealed class CoreConsumerService(AxiosClient client)
{
    /// <summary>按 ID 取用户：注入的是核心 <see cref="AxiosClient"/> 单例，直接用它的强类型 GET。</summary>
    public async Task<UserProfile> GetUserAsync(long id)
    {
        var response = await client.GetAsync<UserProfile>($"api/users/{id}");
        return response.Data!;
    }
}

/// <summary>场景 2/4 的消费方：构造函数注入 <see cref="ApiClient"/> 单例。</summary>
sealed class DeclarativeConsumerService(ApiClient client)
{
    /// <summary>用默认站点取受保护档案：请求模型自带路由与授权声明。</summary>
    public async Task<SecureProfile> GetSecureProfileAsync()
    {
        var response = await client.RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 5 的消费方：构造函数注入 <see cref="IApiClientResolver"/>，运行时按键取站点客户端。</summary>
sealed class MultiSiteDispatcherService(IApiClientResolver resolver)
{
    /// <summary>按站点名取受保护档案：站点在运行期才定，所以走解析器。</summary>
    public async Task<SecureProfile> GetSecureProfileAsync(string site)
    {
        var response = await resolver.Get(site).RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 6 的消费方：构造参数上标 [FromKeyedServices("a")]，编译期绑定站点。</summary>
sealed class KeyedConsumerService([FromKeyedServices("a")] ApiClient clientA)
{
    /// <summary>从键名站点 A 取受保护档案：站点在构造参数上就固定了。</summary>
    public async Task<SecureProfile> GetFromSiteAAsync()
    {
        var response = await clientA.RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 6 的消费方：注入 IServiceProvider，运行期按键取站点客户端。</summary>
sealed class KeyedDispatcherService(IServiceProvider services)
{
    /// <summary>按站点键取指定用户：键从参数传入，用 <c>IServiceProvider</c> 运行期解析。</summary>
    public async Task<UserProfile> GetUserAsync(string site, long id)
    {
        var client = services.GetRequiredKeyedService<ApiClient>(site);
        var response = await client.RequestAsync(new GetUserRequest { Id = (int)id });
        return response.Data!;
    }
}

/// <summary>站点键（enum 作键，避免魔法字符串）。</summary>
enum SiteKey { A, B, C }
