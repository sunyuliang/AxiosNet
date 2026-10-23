using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 3. POST 对象 -> application/json，响应强类型回读。
/// </summary>
public sealed class PostJsonExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 3;

    public string Title => "POST 对象自动 JSON 序列化（/echo 原样回显）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });
        var payload = new User(0, "bob", 30, true, 99.5, new Address("Beijing", "100000"), new[] { "dev" });

        var response = await http.PostAsync<User>("/echo", payload);
        Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
    }
}
