using AxiosNet;
using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Examples;

/// <summary>
/// 5. AddApiClient(name, ...) —— 多站点命名注册 + IApiClientResolver 按站点名取用。
/// </summary>
public sealed class NamedSitesExample : IExample
{
    public int Number => 5;

    public string Title => "AddApiClient(name, ...)：多站点命名注册 + IApiClientResolver";

    public async Task RunAsync(string baseUrl)
    {
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
}
