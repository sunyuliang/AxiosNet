using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>2. axios.create：baseURL / 默认头 / 超时 + 强类型响应。</summary>
public sealed class CreateInstanceExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 2;

    public string Title => "Axios.Create 创建实例（baseURL、默认头、强类型响应）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后所有次运行复用同一实例（连接池随之复用）
        var http = _http ??= Axios.Create(c =>
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
