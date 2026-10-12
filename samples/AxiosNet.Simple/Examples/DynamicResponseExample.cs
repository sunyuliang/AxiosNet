using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 7. dynamic 响应：无需 DTO，直接 data.xxx（对标 JS 用法）。
/// </summary>
public sealed class DynamicResponseExample : IExample
{
    public int Number => 7;

    public string Title => "dynamic 响应：GetAsync<dynamic> 直接 data.name / data.address.city";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient();

        var response = await http.GetAsync<dynamic>(context.BaseUrl + "users/1");
        dynamic user = response.Data!;
        Console.WriteLine($"  name={user.name}, age={user.age}, active={user.active}, score={user.score}");
        Console.WriteLine($"  address.city={user.address.city}, roles[0]={user.roles[0]}, roles[1]={user.roles[1]}");

        // 根数组同样动态可用
        var listResponse = await http.GetAsync<dynamic>(context.BaseUrl + "users");
        dynamic users = listResponse.Data!;
        Console.WriteLine($"  根数组: count={users.Count}, 第一个={users[0].name}, 第二个={users[1].name}");

        // 也可以在非泛型请求上显式使用 ResponseType.Dynamic
        var configured = await http.GetAsync(context.BaseUrl + "users/1", c => c.ResponseType = ResponseType.Dynamic);
        dynamic viaConfig = configured.Data!;
        Console.WriteLine($"  ResponseType.Dynamic 方式: {viaConfig.name}");
    }
}
