using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests;

/// <summary>
/// AddApiInterface：代理单例注册、命名站点解析、容器缺注册时的报错、参数守卫与端到端请求。
/// </summary>
public sealed class ApiInterfaceServiceCollectionTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddApiInterface_RegistersSingletonProxy()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("https://api.example.com");
            services.AddApiInterface<IDiUserApi>();
        });

        var first = provider.GetRequiredService<IDiUserApi>();
        var second = provider.GetRequiredService<IDiUserApi>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddApiInterface_RepeatedRegistration_KeepsFirst()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("https://api.example.com");
            services.AddApiInterface<IDiUserApi>();
            services.AddApiInterface<IDiUserApi>();
        });

        // TryAddSingleton 语义：重复注册不覆盖，也不产生第二条描述符
        Assert.Single(provider.GetServices<IDiUserApi>());
    }

    [Fact]
    public void AddApiInterface_WithoutApiClient_ThrowsOnResolve()
    {
        using var provider = BuildProvider(services =>
            services.AddApiInterface<IDiUserApi>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IDiUserApi>());

        Assert.Contains("AddApiClient", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddApiInterface_Named_WithoutSite_ThrowsOnResolve()
    {
        using var provider = BuildProvider(services =>
            services.AddApiInterface<IDiUserApi>("missing-site"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IDiUserApi>());

        Assert.Contains("missing-site", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddApiInterface_Named_UsesRegisteredSite()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("a", "https://a.example.com");
            services.AddApiClient("b", "https://b.example.com");
            services.AddApiInterface<IDiUserApi>("b");
        });

        // 解析成功即说明代理挂在命名站点 b 上（站点缺失会抛错）
        Assert.NotNull(provider.GetRequiredService<IDiUserApi>());
    }

    [Fact]
    public async Task AddApiInterface_ResolvedProxy_SendsRequestAgainstServer()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
        {
            services.AddApiClient(server.Url);
            services.AddApiInterface<IDiUserApi>();
        });

        var api = provider.GetRequiredService<IDiUserApi>();
        var response = await api.GetAsync(5);

        Assert.Equal(200, response.Status);
        Assert.Equal("alice", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/5", received.Path);
    }

    [Fact]
    public void AddApiInterface_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApiInterface<IDiUserApi>());
        Assert.Same(services, services.AddApiInterface<IDiUserApi>("a"));
    }

    [Fact]
    public void AddApiInterface_BlankName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddApiInterface<IDiUserApi>("  "));
    }

    [Fact]
    public void AddApiInterface_NonInterfaceType_ThrowsAtRegistration()
    {
        // 注册期就拒绝：不要等到容器解析时才暴露"不是接口"
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddApiInterface<NotAnApi>());
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddApiInterface<NotAnApi>("a"));
    }

    [Fact]
    public void AddApiInterface_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddApiInterface<IDiUserApi>());
    }
}

/// <summary>DI 测试用的最小接口声明。</summary>
public interface IDiUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<Person>> GetAsync([Path] int id);
}

/// <summary>故意不是接口：AddApiInterface 的注册期守卫用。</summary>
public sealed class NotAnApi
{
}
