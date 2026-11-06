using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Examples;

/// <summary>
/// 4. 容器授权 —— IApiCredentialProvider 单例自动装配。
/// </summary>
public sealed class ContainerAuthorizationExample : IExample
{
    public int Number => 4;

    public string Title => "容器授权：IApiCredentialProvider 单例自动装配";

    /// <summary>示例入口：容器授权：IApiCredentialProvider 单例自动装配。</summary>
    public async Task RunAsync(string baseUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // 凭据提供方本身也走 DI（真实系统里可从其它服务取令牌 / 刷新）
        builder.Services.AddSingleton<IApiCredentialProvider>(
            CredentialProviders.Fixed(DemoApiServer.DemoToken));

        // 未显式设置 CredentialProvider 时，自动采用容器里的 IApiCredentialProvider 单例
        builder.Services.AddApiClient(baseUrl, configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10));

        builder.Services.AddSingleton<DeclarativeConsumerService>();

        using var host = builder.Build();

        var api = host.Services.GetRequiredService<ApiClient>();
        var credential = host.Services.GetRequiredService<IApiCredentialProvider>();
        Console.WriteLine($"  凭据自动来自容器: {ReferenceEquals(api.CredentialProvider, credential)}");

        var service = host.Services.GetRequiredService<DeclarativeConsumerService>();
        var profile = await service.GetSecureProfileAsync();
        Console.WriteLine($"  注入消费 -> GET api/secure/profile: {profile.User}");
    }
}
