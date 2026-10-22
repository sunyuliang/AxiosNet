using AxiosNet;
using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Scenarios;

/// <summary>
/// 6. 键名服务（.NET 8+ 官方机制）—— AddKeyedSingleton + [FromKeyedServices] / GetRequiredKeyedService。
/// 与场景 5 等价的多站点方案：不经过扩展包的命名重载，直接用容器官方的键名服务。
/// 适合消费方已是 .NET 8+、且希望完全走官方 API（键可以是字符串，也可以是 enum）。
/// </summary>
public sealed class KeyedServicesScenario : IScenario
{
    public int Number => 6;

    public string Title => "键名服务（.NET 8+）：AddKeyedSingleton + [FromKeyedServices]";

    public async Task RunAsync(string baseUrl)
    {
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
}
