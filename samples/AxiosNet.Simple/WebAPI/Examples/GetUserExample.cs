using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 1. 最小可用：定义请求模型 + 一句 RequestAsync，正文类型由请求模型自动确定。
/// </summary>
public sealed class GetUserExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 1;

    public string Title => "GET 详情：[Path] 绑定 + RequestAsync 直接拿正文（api/users/{id}）";

    /// <summary>示例入口：GET 详情：[Path] 绑定 + RequestAsync 直接拿正文（api/users/{id}）。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // BaseAddress 之外零配置；授权在示例 4 才引入，匿名端点直接可用
        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var client = _client ??= new ApiClient(context.BaseUrl);

        var user = await client.RequestAsync(new GetUserRequest { Id = 1 });   // 正文已是 UserProfile
        Console.WriteLine($"  Data = {ExampleConsole.Pretty(user)}");

        // 要读状态码 / 响应头 / 请求消息时用 RequestFullAsync：正文在 response.Data 上
        var response = await client.RequestFullAsync(new GetUserRequest { Id = 1 });
        Console.WriteLine($"  {response.Status} {response.StatusText}"
            + $"，请求行 = {response.Request!.Method} {response.Request.RequestUri}");
    }
}
