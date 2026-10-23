using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 2. 查询参数：无标注属性默认按 Query 绑定，数组走 ids[]= 规则，服务端按参数过滤。
/// </summary>
public sealed class ListUsersExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 2;

    public string Title => "GET 列表：无标注属性默认 Query + 数组绑定（api/users?ids[]=1&ids[]=2&role=...）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var client = _client ??= new ApiClient(context.BaseUrl);

        var all = await client.RequestAsync(new ListUsersRequest());
        Console.WriteLine($"  全部 {all.Data!.Length} 人 -> {ExampleConsole.Pretty(all.Data)}");

        var filtered = await client.RequestAsync(new ListUsersRequest { Ids = [1, 2], Role = "dev" });
        Console.WriteLine($"  role=dev 过滤 -> {ExampleConsole.Pretty(filtered.Data)}");

        // client.GetUri(request)：只拼 URL 不发请求（日志 / 签名用）
        Console.WriteLine($"  GetUri -> {client.GetUri(new ListUsersRequest { Ids = [1, 2] })}");
    }
}
