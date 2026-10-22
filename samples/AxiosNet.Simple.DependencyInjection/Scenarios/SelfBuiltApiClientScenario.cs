using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Scenarios;

/// <summary>
/// 2. AddApiClient(baseAddress, ...) —— 自带 BaseAddress 自建底层客户端。
/// </summary>
public sealed class SelfBuiltApiClientScenario : IScenario
{
    public int Number => 2;

    public string Title => "AddApiClient(baseAddress, ...)：自建底层客户端";

    public async Task RunAsync(string baseUrl)
    {
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
}
