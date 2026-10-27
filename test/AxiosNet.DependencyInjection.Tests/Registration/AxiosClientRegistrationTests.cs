using AxiosNet.DependencyInjection;
using AxiosNet.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests.Registration;

/// <summary>
/// AddAxiosClient 单例注入：注册形态、configure 生效、TryAdd 语义、容器解析后的端到端请求。
/// </summary>
public sealed class AxiosClientRegistrationTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddAxiosClient_RegistersSingleton_SameInstanceAcrossResolutions()
    {
        using var provider = BuildProvider(services => services.AddAxiosClient());

        var first = provider.GetRequiredService<AxiosClient>();
        var second = provider.GetRequiredService<AxiosClient>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddAxiosClient_NoConfigure_UsesFreshDefaults()
    {
        using var provider = BuildProvider(services => services.AddAxiosClient());

        var client = provider.GetRequiredService<AxiosClient>();

        Assert.NotNull(client.Defaults);
        Assert.Null(client.Defaults.BaseAddress);
        Assert.True(client.UsesConnectionPool);
    }

    [Fact]
    public void AddAxiosClient_Configure_AppliedToInstanceDefaults()
    {
        using var provider = BuildProvider(services => services.AddAxiosClient(config =>
        {
            config.BaseAddress = "https://api.example.com";
            config.Timeout = TimeSpan.FromSeconds(3);
            config.Headers.Set("X-Trace", "di");
        }));

        var defaults = provider.GetRequiredService<AxiosClient>().Defaults;

        Assert.Equal("https://api.example.com", defaults.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(3), defaults.Timeout);
        Assert.Equal("di", defaults.Headers.Get("X-Trace"));
    }

    [Fact]
    public async Task AddAxiosClient_ResolvedClient_SendsRequestsAgainstServer()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services => services.AddAxiosClient(config =>
        {
            config.BaseAddress = server.Url;
        }));

        var client = provider.GetRequiredService<AxiosClient>();
        var response = await client.GetAsync<Person>("users/1");

        Assert.Equal(200, response.Status);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal("/users/1", Assert.Single(server.Received).Path);
    }

    [Fact]
    public void AddAxiosClient_CalledTwice_KeepsFirstRegistration()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddAxiosClient(config => config.BaseAddress = "https://first.example.com");
            services.AddAxiosClient(config => config.BaseAddress = "https://second.example.com");
        });

        Assert.Equal(
            "https://first.example.com",
            provider.GetRequiredService<AxiosClient>().Defaults.BaseAddress);
    }

    [Fact]
    public void AddAxiosClient_CalledTwice_SecondConfigureDoesNotRun()
    {
        // TryAdd 语义必须连 configure 的副作用一起跳过：第二次「不生效」是完整的，
        // 不能只丢注册却把 configure 跑一遍（序列化器注册、诊断埋点会重复生效）
        var calls = 0;
        var services = new ServiceCollection();

        services.AddAxiosClient(_ => calls++);
        services.AddAxiosClient(_ => calls++);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AddAxiosClient_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddAxiosClient());
    }

    [Fact]
    public void AddAxiosClient_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddAxiosClient());
    }
}
