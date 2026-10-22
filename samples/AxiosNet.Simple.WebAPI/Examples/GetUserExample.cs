using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 1. 最小可用：定义请求模型 + 一句 RequestAsync，返回类型由请求模型自动确定。
/// </summary>
public sealed class GetUserExample : IExample
{
    public int Number => 1;

    public string Title => "GET 详情：[Path] 绑定 + 泛型自动确定（api/users/{id}）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // BaseAddress 之外零配置；授权在示例 4 才引入，匿名端点直接可用
        using var client = new ApiClient(context.BaseUrl);

        var response = await client.RequestAsync(new GetUserRequest { Id = 1 });

        Console.WriteLine($"  {response.Status} {response.StatusText}");
        Console.WriteLine($"  Data = {ExampleConsole.Pretty(response.Data)}");
        Console.WriteLine($"  请求行 = {response.Request!.Method} {response.Request.RequestUri}");
    }
}
