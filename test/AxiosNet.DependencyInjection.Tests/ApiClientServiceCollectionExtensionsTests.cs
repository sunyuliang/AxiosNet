using AxiosNet.DependencyInjection;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using AxiosNet.Tests.WebAPI;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests;

/// <summary>
/// AddApiClient 单例注入：自建 / 门面两种模式、configure 与容器授权装配、TryAdd 语义、端到端请求。
/// </summary>
public sealed class ApiClientServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    // ---- 自建模式：AddApiClient(baseAddress, ...) ----

    [Fact]
    public void AddApiClient_WithBaseAddress_RegistersSingleton()
    {
        using var provider = BuildProvider(services =>
            services.AddApiClient("https://api.example.com"));

        var first = provider.GetRequiredService<ApiClient>();
        var second = provider.GetRequiredService<ApiClient>();

        Assert.Same(first, second);
        Assert.Equal("https://api.example.com", first.Axios.Defaults.BaseAddress);
    }

    [Fact]
    public void AddApiClient_ConfigureDefaults_AppliedToUnderlyingClient()
    {
        using var provider = BuildProvider(services =>
            services.AddApiClient("https://api.example.com", configureDefaults: config =>
            {
                config.Timeout = TimeSpan.FromSeconds(7);
                config.Headers.Set("X-App", "di");
            }));

        var defaults = provider.GetRequiredService<ApiClient>().Axios.Defaults;

        Assert.Equal(TimeSpan.FromSeconds(7), defaults.Timeout);
        Assert.Equal("di", defaults.Headers.Get("X-App"));
    }

    [Fact]
    public async Task AddApiClient_ConfigureAuthorization_HeaderSentWithRequest()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
            services.AddApiClient(server.Url,
                configure: client => client.Authorization = new BearerAuthorizationProvider("token-1")));

        var client = provider.GetRequiredService<ApiClient>();
        var response = await client.RequestAsync(new GetUserRequest { Id = 5 });

        Assert.Equal(200, response.Status);
        Assert.Equal("Bearer token-1", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AddApiClient_AuthorizationProviderFromContainer_AutoWired()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        var fromContainer = new BearerAuthorizationProvider("token-from-di");

        using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IAuthorizationProvider>(fromContainer);
            services.AddApiClient(server.Url);
        });

        var client = provider.GetRequiredService<ApiClient>();
        Assert.Same(fromContainer, client.Authorization);

        var response = await client.RequestAsync(new GetUserRequest { Id = 5 });

        Assert.Equal(200, response.Status);
        Assert.Equal("Bearer token-from-di", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public void AddApiClient_ConfigureAuthorization_WinsOverContainerProvider()
    {
        var fromContainer = new BearerAuthorizationProvider("from-container");
        var fromConfigure = new BearerAuthorizationProvider("from-configure");

        using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IAuthorizationProvider>(fromContainer);
            services.AddApiClient("https://api.example.com",
                configure: client => client.Authorization = fromConfigure);
        });

        Assert.Same(fromConfigure, provider.GetRequiredService<ApiClient>().Authorization);
    }

    [Fact]
    public void AddApiClient_WithBaseAddress_IndependentOfRegisteredAxiosClient()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddAxiosClient(config => config.BaseAddress = "https://registered.example.com");
            services.AddApiClient("https://self-contained.example.com");
        });

        var axiosClient = provider.GetRequiredService<AxiosClient>();
        var apiClient = provider.GetRequiredService<ApiClient>();

        Assert.NotSame(axiosClient, apiClient.Axios);
        Assert.Equal("https://registered.example.com", axiosClient.Defaults.BaseAddress);
        Assert.Equal("https://self-contained.example.com", apiClient.Axios.Defaults.BaseAddress);
    }

    [Fact]
    public void AddApiClient_CalledTwice_KeepsFirstRegistration()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("https://first.example.com");
            services.AddApiClient("https://second.example.com");
        });

        Assert.Equal(
            "https://first.example.com",
            provider.GetRequiredService<ApiClient>().Axios.Defaults.BaseAddress);
    }

    [Fact]
    public async Task AddApiClient_ResolvedClient_SendsRequestsAgainstServer()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
            services.AddApiClient(server.Url, configureDefaults: config => config.Headers.Set("X-App", "di")));

        var client = provider.GetRequiredService<ApiClient>();
        var response = await client.RequestAsync(new GetUserRequest { Id = 5, Trace = "trace-di" });

        Assert.Equal(200, response.Status);
        Assert.Equal("alice", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/5", received.Path);
        Assert.Equal("di", received.Headers["X-App"]);
        Assert.Equal("trace-di", received.Headers["X-Trace"]);
    }

    // ---- 门面模式：AddApiClient(configure) 复用已注册的 AxiosClient ----

    [Fact]
    public async Task AddApiClient_Facade_WrapsRegisteredAxiosClient()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
        {
            services.AddAxiosClient(config => config.BaseAddress = server.Url);
            services.AddApiClient(configure: client =>
                client.Authorization = new BearerAuthorizationProvider("facade-token"));
        });

        var axiosClient = provider.GetRequiredService<AxiosClient>();
        var apiClient = provider.GetRequiredService<ApiClient>();

        Assert.Same(axiosClient, apiClient.Axios);

        var response = await apiClient.RequestAsync(new GetUserRequest { Id = 5 });

        Assert.Equal(200, response.Status);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal("Bearer facade-token", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public void AddApiClient_Facade_WithoutRegisteredAxiosClient_ThrowsOnResolve()
    {
        using var provider = BuildProvider(services => services.AddApiClient());

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ApiClient>());

        Assert.Contains("AddAxiosClient", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddApiClient_Facade_Dispose_DoesNotDisposeSharedAxiosClient()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
        {
            services.AddAxiosClient(config => config.BaseAddress = server.Url);
            services.AddApiClient();
        });

        var apiClient = provider.GetRequiredService<ApiClient>();
        apiClient.Dispose();

        // 共享的 AxiosClient 单例不随门面释放，继续可用
        var response = await provider.GetRequiredService<AxiosClient>().GetAsync<Person>("users/1");

        Assert.Equal(200, response.Status);
    }

    // ---- 参数守卫与链式返回 ----

    [Fact]
    public void AddApiClient_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApiClient("https://api.example.com"));
        Assert.Same(services, services.AddApiClient());
    }

    [Fact]
    public void AddApiClient_BlankBaseAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddApiClient("  "));
    }

    [Fact]
    public void AddApiClient_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddApiClient("https://api.example.com"));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddApiClient());
    }
}
