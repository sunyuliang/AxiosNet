using AxiosNet.DependencyInjection;
using AxiosNet.Simple.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiosNet.Simple.DependencyInjection.Examples;

/// <summary>
/// 3. AddApiClient(configure) —— 门面复用已注册的 AxiosClient。
/// </summary>
public sealed class FacadeApiClientExample : IExample
{
    public int Number => 3;

    public string Title => "AddApiClient(configure)：门面复用已注册的 AxiosClient";

    public async Task RunAsync(string baseUrl)
    {
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
}
