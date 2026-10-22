using AxiosNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Scenarios;

/// <summary>
/// 1. AddAxiosClient —— 核心包客户端单例（AxiosNet.DependencyInjection）。
/// </summary>
public sealed class CoreClientSingletonScenario : IScenario
{
    public int Number => 1;

    public string Title => "AddAxiosClient：核心客户端单例";

    public async Task RunAsync(string baseUrl)
    {
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
}
