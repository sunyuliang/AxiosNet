using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 3. 请求体：[Body] 整个 Payload 走 JSON；模型 Configure 钩子加的 X-Trace 头被服务器回显。
/// </summary>
public sealed class CreateUserExample : IExample
{
    public int Number => 3;

    public string Title => "POST 创建：[Body] 请求体 + 模型 Configure 钩子（X-Trace 回显验证）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        using var client = new ApiClient(context.BaseUrl);

        var response = await client.RequestAsync(new CreateUserRequest
        {
            Payload = new UserPayload("carol", 25)
        });

        Console.WriteLine($"  {response.Status} {response.StatusText}");
        var created = response.Data!;
        Console.WriteLine($"  Data = {ExampleConsole.Pretty(created)}");
        Console.WriteLine($"  服务器回显 X-Trace = {created.Trace}（由 CreateUserRequest.Configure 写入）");
    }
}
