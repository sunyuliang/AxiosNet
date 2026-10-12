using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 2. axios.create：baseURL / 默认头 / 超时 + 强类型响应。
/// </summary>
public sealed class CreateInstanceExample : IExample
{
    public int Number => 2;

    public string Title => "Axios.Create 创建实例（baseURL、默认头、强类型响应）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var http = Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Timeout = TimeSpan.FromSeconds(10);
            c.Headers.Set("X-Client", "AxiosNet.Simple");
        });

        var response = await http.GetAsync<User>("/users/1");
        var user = response.Data!;
        Console.WriteLine($"  {response.Status} -> {user.Name}, {user.Age} 岁, 城市 {user.Address!.City}");
        Console.WriteLine($"  角色: {string.Join("/", user.Roles!)}");
    }
}
