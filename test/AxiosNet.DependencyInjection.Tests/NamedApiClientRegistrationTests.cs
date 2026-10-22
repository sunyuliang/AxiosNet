using AxiosNet;
using AxiosNet.DependencyInjection;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using AxiosNet.Tests.WebAPI;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests;

/// <summary>
/// AddApiClient(name, ...) 命名站点注册：多站点解析、重名/未注册报错、per-site 配置、
/// 容器授权装配、与既有重载共存、端到端请求与释放。
/// </summary>
public sealed class NamedApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    // ---- 注册形态与解析 ----

    [Fact]
    public void AddApiClient_WithName_RegistersResolverAndSiteClient()
    {
        using var provider = BuildProvider(services =>
            services.AddApiClient("a", "https://a.example.com"));

        var resolver = provider.GetRequiredService<IApiClientResolver>();
        var client = resolver.Get("a");

        Assert.Equal("https://a.example.com", client.Axios.Defaults.BaseAddress);
        Assert.Same(client, resolver.Get("a"));
    }

    [Fact]
    public void AddApiClient_Named_ConfigureThrows_PropagatesWithoutCorruptingOtherSites()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("good", "https://good.example.com");
            services.AddApiClient("bad", "https://bad.example.com",
                configure: _ => throw new InvalidOperationException("boom"));
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        // 站点 configure 抛错原样透出（此前 holder 先登记后创建，失败时每次重试
        // 都新建一个连接池留在 _owned 里直到容器退出才释放）
        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Get("bad"));
        Assert.Equal("boom", ex.Message);

        // 失败不污染其他站点：好站点照常可取可用
        Assert.Equal("https://good.example.com", resolver.Get("good").Axios.Defaults.BaseAddress);
    }

    [Fact]
    public void AddApiClient_NamedSites_ResolveToDistinctClientsWithOwnBaseAddress()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("a", "https://a.example.com");
            services.AddApiClient("b", "https://b.example.com");
            services.AddApiClient("c", "https://c.example.com");
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();
        var a = resolver.Get("a");
        var b = resolver.Get("b");
        var c = resolver.Get("c");

        Assert.NotSame(a, b);
        Assert.NotSame(b, c);
        Assert.NotSame(a.Axios, b.Axios);
        Assert.Equal("https://a.example.com", a.Axios.Defaults.BaseAddress);
        Assert.Equal("https://b.example.com", b.Axios.Defaults.BaseAddress);
        Assert.Equal("https://c.example.com", c.Axios.Defaults.BaseAddress);
    }

    [Fact]
    public void AddApiClient_Named_NamesInRegistrationOrder()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("c", "https://c.example.com");
            services.AddApiClient("a", "https://a.example.com");
            services.AddApiClient("b", "https://b.example.com");
        });

        Assert.Equal(new[] { "c", "a", "b" }, provider.GetRequiredService<IApiClientResolver>().Names);
    }

    [Fact]
    public void AddApiClient_Named_TryGet_ExistingAndMissing()
    {
        using var provider = BuildProvider(services =>
            services.AddApiClient("a", "https://a.example.com"));

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        Assert.True(resolver.TryGet("a", out var found));
        Assert.Same(resolver.Get("a"), found);

        // 站点名区分大小写
        Assert.False(resolver.TryGet("A", out _));
        Assert.False(resolver.TryGet("missing", out _));
        Assert.False(resolver.TryGet(null!, out _));
    }

    [Fact]
    public void AddApiClient_Named_GetUnknown_ThrowsWithAvailableNames()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("a", "https://a.example.com");
            services.AddApiClient("b", "https://b.example.com");
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Get("zz"));

        Assert.Contains("zz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => resolver.Get(null!));
    }

    [Fact]
    public void AddApiClient_DuplicateName_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddApiClient("a", "https://a.example.com");

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddApiClient("a", "https://a-other.example.com"));

        Assert.Contains("a", ex.Message, StringComparison.Ordinal);
    }

    // ---- per-site 配置与容器授权 ----

    [Fact]
    public async Task AddApiClient_Named_ConfigureDefaults_AppliedPerSite()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("a", server.Url, configureDefaults: config =>
            {
                config.Timeout = TimeSpan.FromSeconds(7);
                config.Headers.Set("X-Site", "a");
            });
            services.AddApiClient("b", server.Url, configureDefaults: config =>
            {
                config.Timeout = TimeSpan.FromSeconds(11);
                config.Headers.Set("X-Site", "b");
            });
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        Assert.Equal(TimeSpan.FromSeconds(7), resolver.Get("a").Axios.Defaults.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(11), resolver.Get("b").Axios.Defaults.Timeout);

        await resolver.Get("a").RequestAsync(new GetUserRequest { Id = 5 });
        await resolver.Get("b").RequestAsync(new GetUserRequest { Id = 6 });

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("a", server.Received[0].Headers["X-Site"]);
        Assert.Equal("b", server.Received[1].Headers["X-Site"]);
    }

    [Fact]
    public async Task AddApiClient_Named_AuthorizationProviderFromContainer_AutoWired()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        var fromContainer = new BearerAuthorizationProvider("token-from-di");

        using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IAuthorizationProvider>(fromContainer);
            services.AddApiClient("a", server.Url);
            services.AddApiClient("b", server.Url);
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        Assert.Same(fromContainer, resolver.Get("a").Authorization);
        Assert.Same(fromContainer, resolver.Get("b").Authorization);

        await resolver.Get("a").RequestAsync(new GetUserRequest { Id = 5 });
        await resolver.Get("b").RequestAsync(new GetUserRequest { Id = 6 });

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, received =>
            Assert.Equal("Bearer token-from-di", received.Headers["Authorization"]));
    }

    [Fact]
    public async Task AddApiClient_Named_ConfigureAuthorization_WinsOverContainerProvider()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        var fromContainer = new BearerAuthorizationProvider("from-container");

        using var provider = BuildProvider(services =>
        {
            services.AddSingleton<IAuthorizationProvider>(fromContainer);
            services.AddApiClient("a", server.Url,
                configure: client => client.Authorization = new BearerAuthorizationProvider("token-a"));
            services.AddApiClient("b", server.Url,
                configure: client => client.Authorization = new BearerAuthorizationProvider("token-b"));
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        await resolver.Get("a").RequestAsync(new GetUserRequest { Id = 5 });
        await resolver.Get("b").RequestAsync(new GetUserRequest { Id = 6 });

        // 每站点授权相互独立：容器提供方只兜底，显式配置的按站点生效
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer token-a", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer token-b", server.Received[1].Headers["Authorization"]);
    }

    // ---- 与既有重载的共存关系 ----

    [Fact]
    public void AddApiClient_Named_DoesNotRegisterUnkeyedApiClient()
    {
        using var provider = BuildProvider(services =>
            services.AddApiClient("a", "https://a.example.com"));

        // 命名站点只经解析器暴露；单站点场景用不带 name 的重载
        Assert.Null(provider.GetService<ApiClient>());
        Assert.NotNull(provider.GetRequiredService<IApiClientResolver>());
    }

    [Fact]
    public void AddApiClient_Named_CoexistsWithFacadeRegistration()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddAxiosClient(config => config.BaseAddress = "https://registered.example.com");
            services.AddApiClient("a", "https://a.example.com");
            services.AddApiClient(configure: client =>
                client.Authorization = new BearerAuthorizationProvider("facade-token"));
        });

        var facade = provider.GetRequiredService<ApiClient>();
        var siteA = provider.GetRequiredService<IApiClientResolver>().Get("a");

        Assert.Equal("https://registered.example.com", facade.Axios.Defaults.BaseAddress);
        Assert.Equal("https://a.example.com", siteA.Axios.Defaults.BaseAddress);
        Assert.NotSame(facade, siteA);
        Assert.NotSame(facade.Axios, siteA.Axios);
    }

    // ---- 端到端与释放 ----

    [Fact]
    public async Task AddApiClient_Named_ResolvedClients_SendRequestsAgainstServer()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Path == "/users/5"
                ? TestJson.Response(new Person("alice", 18))
                : TestJson.Response(new Person("bob", 30)));

        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("a", server.Url);
            services.AddApiClient("b", server.Url);
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        var fromA = await resolver.Get("a").RequestAsync(new GetUserRequest { Id = 5 });
        var fromB = await resolver.Get("b").RequestAsync(new GetUserRequest { Id = 6 });

        Assert.Equal(200, fromA.Status);
        Assert.Equal("alice", fromA.Data!.Name);
        Assert.Equal(200, fromB.Status);
        Assert.Equal("bob", fromB.Data!.Name);
    }

    [Fact]
    public async Task AddApiClient_Named_ResolverDisposal_ReleasesSiteClients()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var provider = BuildProvider(services =>
            services.AddApiClient("a", server.Url));

        var resolver = provider.GetRequiredService<IApiClientResolver>();
        var client = resolver.Get("a");

        var response = await client.RequestAsync(new GetUserRequest { Id = 5 });
        Assert.Equal(200, response.Status);

        // 解析器是各站点自建客户端的属主：释放后站点客户端不再可用（连接池不泄漏）
        ((IDisposable)resolver).Dispose();

        // 释放后 Get 已解析站点：直接抛 ObjectDisposedException，不返回已销毁的客户端
        // （此前返回死客户端，错误要到发送请求时才以 ObjectDisposedException 形式漏出）
        Assert.Throws<ObjectDisposedException>(() => resolver.Get("a"));

        // 释放后 Get 从未解析过的站点同样抛错：否则会新建一个没有属主的客户端，连接池直接泄漏
        Assert.Throws<ObjectDisposedException>(() => resolver.Get("never-resolved"));
    }

    // ---- 参数守卫与链式返回 ----

    [Fact]
    public void AddApiClient_Named_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApiClient("a", "https://a.example.com"));
    }

    [Fact]
    public void AddApiClient_Named_BlankNameOrBaseAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddApiClient("  ", "https://a.example.com"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddApiClient("a", "  "));
    }

    [Fact]
    public void AddApiClient_Named_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddApiClient("a", "https://a.example.com"));
    }
}
