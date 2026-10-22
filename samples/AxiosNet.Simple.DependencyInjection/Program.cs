using AxiosNet;
using AxiosNet.DependencyInjection;
using AxiosNet.Simple.WebAPI; // 链接自 AxiosNet.Simple.WebAPI 的演示服务器与请求/返回模型
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// AxiosNet.Simple.DependencyInjection —— AxiosNet.DependencyInjection 扩展示例
// 六个场景各建一个 Host：同一容器内单例只能注册一次（TryAdd 先到先得），场景间互相隔离。
//   1) AddAxiosClient                   核心客户端单例
//   2) AddApiClient 自建模式            自带 BaseAddress 自建底层客户端
//   3) AddApiClient 门面模式            复用 AddAxiosClient 注册的单例
//   4) 容器授权                         IAuthorizationProvider 单例自动装配
//   5) AddApiClient(name, ...) 多站点   命名注册 + IApiClientResolver 按站点名取用
//   6) 键名服务（.NET 8+ 官方机制）      AddKeyedSingleton + [FromKeyedServices] / GetRequiredKeyedService
// 示例自带本地 HTTP/1.1 Keep-Alive 演示服务器，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple.DependencyInjection

using var server = DemoApiServer.Start();
Console.WriteLine($"本地演示服务器: {server.Url}");

await Scenario1_CoreClientSingletonAsync(server.Url);
await Scenario2_SelfBuiltApiClientAsync(server.Url);
await Scenario3_FacadeApiClientAsync(server.Url);
await Scenario4_ContainerAuthorizationAsync(server.Url);
await Scenario5_NamedSitesAsync(server.Url);
await Scenario6_KeyedServicesAsync(server.Url);

Console.WriteLine();
Console.WriteLine("== 全部场景运行完成 ==");

// ------------------------------------------------------------------
// 场景 1：AddAxiosClient —— 核心包客户端单例（AxiosNet.DependencyInjection）
// ------------------------------------------------------------------
static async Task Scenario1_CoreClientSingletonAsync(string baseUrl)
{
    PrintScenario(1, "AddAxiosClient：核心客户端单例");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // configure 配实例默认值（BaseAddress / 超时 / 头 / 序列化器 / 连接池选项）
    builder.Services.AddAxiosClient(config =>
    {
        config.BaseAddress = baseUrl;
        config.Timeout = TimeSpan.FromSeconds(10);
    });

    // 业务类按常规注册即可，构造函数注入上面的客户端单例
    builder.Services.AddSingleton<CoreConsumerService>();

    using var host = builder.Build();

    // 直接解析：两次拿到同一实例（单例，连接池随容器生命周期共享）
    var first = host.Services.GetRequiredService<AxiosClient>();
    var second = host.Services.GetRequiredService<AxiosClient>();
    Console.WriteLine($"  两次解析同一实例: {ReferenceEquals(first, second)}");

    // 或注入进业务类
    var service = host.Services.GetRequiredService<CoreConsumerService>();
    var user = await service.GetUserAsync(1);
    Console.WriteLine($"  注入消费 -> GET api/users/1: {user.Name} ({user.Age}, {user.Role})");
}

// ------------------------------------------------------------------
// 场景 2：AddApiClient(baseAddress, ...) —— 自建底层客户端
// ------------------------------------------------------------------
static async Task Scenario2_SelfBuiltApiClientAsync(string baseUrl)
{
    PrintScenario(2, "AddApiClient(baseAddress, ...)：自建底层客户端");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // configureDefaults 配底层 AxiosClient 默认值；configure 配 ApiClient 自身（授权等）
    builder.Services.AddApiClient(baseUrl,
        configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
        configure: client => client.Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken));

    builder.Services.AddSingleton<DeclarativeConsumerService>();

    using var host = builder.Build();

    var service = host.Services.GetRequiredService<DeclarativeConsumerService>();
    var profile = await service.GetSecureProfileAsync();
    Console.WriteLine($"  注入消费 -> GET api/secure/profile: {profile.User} (device={profile.Device})");
}

// ------------------------------------------------------------------
// 场景 3：AddApiClient(configure) —— 门面复用已注册的 AxiosClient
// ------------------------------------------------------------------
static async Task Scenario3_FacadeApiClientAsync(string baseUrl)
{
    PrintScenario(3, "AddApiClient(configure)：门面复用已注册的 AxiosClient");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // 先注册核心客户端，再让声明式门面复用它（连接池 / 拦截器 / 实例默认值全共享）
    builder.Services.AddAxiosClient(config =>
    {
        config.BaseAddress = baseUrl;
        config.Timeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddApiClient(configure: client =>
        client.Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken));

    using var host = builder.Build();

    var axios = host.Services.GetRequiredService<AxiosClient>();
    var api = host.Services.GetRequiredService<ApiClient>();
    Console.WriteLine($"  门面与注册的 AxiosClient 同一实例: {ReferenceEquals(axios, api.Axios)}");

    var users = await api.RequestAsync(new ListUsersRequest { Role = "admin" });
    Console.WriteLine($"  注入消费 -> GET api/users?role=admin: {users.Data!.Length} 人");
}

// ------------------------------------------------------------------
// 场景 4：容器授权 —— IAuthorizationProvider 单例自动装配
// ------------------------------------------------------------------
static async Task Scenario4_ContainerAuthorizationAsync(string baseUrl)
{
    PrintScenario(4, "容器授权：IAuthorizationProvider 单例自动装配");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // 授权提供方本身也走 DI（真实系统里可从其它服务取令牌 / 刷新）
    builder.Services.AddSingleton<IAuthorizationProvider>(
        new BearerAuthorizationProvider(DemoApiServer.DemoToken));

    // 未显式设置 Authorization 时，自动采用容器里的 IAuthorizationProvider 单例
    builder.Services.AddApiClient(baseUrl, configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10));

    builder.Services.AddSingleton<DeclarativeConsumerService>();

    using var host = builder.Build();

    var api = host.Services.GetRequiredService<ApiClient>();
    Console.WriteLine($"  Authorization 自动来自容器: {api.Authorization is BearerAuthorizationProvider}");

    var service = host.Services.GetRequiredService<DeclarativeConsumerService>();
    var profile = await service.GetSecureProfileAsync();
    Console.WriteLine($"  注入消费 -> GET api/secure/profile: {profile.User}");
}

// ------------------------------------------------------------------
// 场景 5：AddApiClient(name, ...) —— 多站点命名注册 + IApiClientResolver
// ------------------------------------------------------------------
static async Task Scenario5_NamedSitesAsync(string baseUrl)
{
    PrintScenario(5, "AddApiClient(name, ...)：多站点命名注册 + IApiClientResolver");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // 站点 a：配好授权与站点标识头（演示服务器对带 DemoToken 的请求返回 200）
    builder.Services.AddApiClient("a", baseUrl,
        configureDefaults: config =>
        {
            config.Timeout = TimeSpan.FromSeconds(10);
            config.Headers.Set("X-Site", "a");
        },
        configure: client => client.Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken));

    // 站点 b：另一个站点，授权各自独立（这里故意不配，演示服务器返回 401）
    builder.Services.AddApiClient("b", baseUrl,
        configureDefaults: config =>
        {
            config.Timeout = TimeSpan.FromSeconds(30);
            config.Headers.Set("X-Site", "b");
        });

    // 消费方只依赖解析器，站点名运行时才知道（真实系统里来自配置 / 数据库）
    builder.Services.AddSingleton<MultiSiteDispatcherService>();

    using var host = builder.Build();

    var resolver = host.Services.GetRequiredService<IApiClientResolver>();
    Console.WriteLine($"  已注册站点: {string.Join(", ", resolver.Names)}");

    var dispatcher = host.Services.GetRequiredService<MultiSiteDispatcherService>();
    var profile = await dispatcher.GetSecureProfileAsync("a");
    Console.WriteLine($"  站点 a -> GET api/secure/profile: {profile.User} (device={profile.Device})");

    // 站点 b 未配授权：401 走 AxiosException，每站点授权相互独立、互不影响
    try
    {
        await dispatcher.GetSecureProfileAsync("b");
    }
    catch (AxiosException ex)
    {
        Console.WriteLine($"  站点 b -> status={ex.Status} {ex.Code}（未配授权，符合预期）");
    }

    // TryGet 不会抛；取未注册的站点名则解析期报错并列出可用站点
    Console.WriteLine($"  TryGet(\"zz\") = {resolver.TryGet("zz", out _)}");
    try
    {
        resolver.Get("zz");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"  Get(\"zz\") -> {ex.Message}");
    }
}

// ------------------------------------------------------------------
// 场景 6：键名服务（.NET 8+ 官方机制）—— AddKeyedSingleton + [FromKeyedServices]
// 与场景 5 等价的多站点方案：不经过扩展包的命名重载，直接用容器官方的键名服务。
// 适合消费方已是 .NET 8+、且希望完全走官方 API（键可以是字符串，也可以是 enum）。
// ------------------------------------------------------------------
static async Task Scenario6_KeyedServicesAsync(string baseUrl)
{
    PrintScenario(6, "键名服务（.NET 8+）：AddKeyedSingleton + [FromKeyedServices]");

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    // 键名注册：同类型可按键注册多个单例（场景 5 的命名重载在内部就是同一思路的 net6.0 实现）
    builder.Services.AddKeyedSingleton<ApiClient>("a", (_, _) => new ApiClient(baseUrl)
    {
        Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken)
    });
    builder.Services.AddKeyedSingleton<ApiClient>("b", (_, _) => new ApiClient(baseUrl));

    // 键不限于字符串：enum 作键没有魔法字符串，重构时编译器会提醒
    builder.Services.AddKeyedSingleton<AxiosClient>(SiteKey.C,
        (_, _) => new AxiosClient(new AxiosConfig { BaseAddress = baseUrl }));

    builder.Services.AddSingleton<KeyedConsumerService>();
    builder.Services.AddSingleton<KeyedDispatcherService>();

    using var host = builder.Build();

    // 1) 编译期注入：构造参数上标 [FromKeyedServices("a")]，要用哪几个站点一目了然
    var consumer = host.Services.GetRequiredService<KeyedConsumerService>();
    var profile = await consumer.GetFromSiteAAsync();
    Console.WriteLine($"  [FromKeyedServices(\"a\")] -> GET api/secure/profile: {profile.User}");

    // 2) 运行期按键取：站点名来自配置 / 数据库时注入 IServiceProvider 或直接解析
    var dispatcher = host.Services.GetRequiredService<KeyedDispatcherService>();
    var user = await dispatcher.GetUserAsync("b", 1);
    Console.WriteLine($"  GetRequiredKeyedService<ApiClient>(\"b\") -> GET api/users/1: {user.Name}");

    // 3) enum 键：与字符串键完全等价，只是编译期更安全
    var core = host.Services.GetRequiredKeyedService<AxiosClient>(SiteKey.C);
    Console.WriteLine($"  GetRequiredKeyedService<AxiosClient>(SiteKey.C) -> BaseAddress: {core.Defaults.BaseAddress}");

    // 4) 未注册的键：解析期抛 InvalidOperationException（容器官方语义）
    try
    {
        host.Services.GetRequiredKeyedService<ApiClient>("zz");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"  取未注册键 \"zz\" -> {ex.Message}");
    }

    // 生命周期：键名单例随容器退出统一释放；new ApiClient(baseAddress) 自管底层客户端，
    // Dispose 会一并释放连接池（与场景 2 的扩展自建模式一致）
}

static void PrintScenario(int number, string title)
{
    Console.WriteLine();
    Console.WriteLine($"== 场景 {number}: {title} ==");
}

/// <summary>场景 1 的消费方：构造函数注入 <see cref="AxiosClient"/> 单例。</summary>
sealed class CoreConsumerService(AxiosClient client)
{
    public async Task<UserProfile> GetUserAsync(long id)
    {
        var response = await client.GetAsync<UserProfile>($"api/users/{id}");
        return response.Data!;
    }
}

/// <summary>场景 2/4 的消费方：构造函数注入 <see cref="ApiClient"/> 单例。</summary>
sealed class DeclarativeConsumerService(ApiClient client)
{
    public async Task<SecureProfile> GetSecureProfileAsync()
    {
        var response = await client.RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 5 的消费方：构造函数注入 <see cref="IApiClientResolver"/>，运行时按键取站点客户端。</summary>
sealed class MultiSiteDispatcherService(IApiClientResolver resolver)
{
    public async Task<SecureProfile> GetSecureProfileAsync(string site)
    {
        var response = await resolver.Get(site).RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 6 的消费方：构造参数上标 [FromKeyedServices("a")]，编译期绑定站点。</summary>
sealed class KeyedConsumerService([FromKeyedServices("a")] ApiClient clientA)
{
    public async Task<SecureProfile> GetFromSiteAAsync()
    {
        var response = await clientA.RequestAsync(new SecureProfileRequest());
        return response.Data!;
    }
}

/// <summary>场景 6 的消费方：注入 IServiceProvider，运行期按键取站点客户端。</summary>
sealed class KeyedDispatcherService(IServiceProvider services)
{
    public async Task<UserProfile> GetUserAsync(string site, long id)
    {
        var client = services.GetRequiredKeyedService<ApiClient>(site);
        var response = await client.RequestAsync(new GetUserRequest { Id = (int)id });
        return response.Data!;
    }
}

/// <summary>站点键（enum 作键，避免魔法字符串）。</summary>
enum SiteKey { A, B, C }
