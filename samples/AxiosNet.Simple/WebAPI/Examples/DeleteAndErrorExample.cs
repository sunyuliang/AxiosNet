using AxiosNet;
using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 5. 删除与错误：方法由路由特性决定；404 走 AxiosNet 统一错误模型（ERR_BAD_REQUEST + Response）。
/// </summary>
public sealed class DeleteAndErrorExample : IExample
{
    public int Number => 5;

    public string Title => "DELETE + 错误处理：非 2xx 抛 AxiosException（ERR_BAD_REQUEST，带 Response）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        using var client = new ApiClient(context.BaseUrl);

        var deleted = await client.RequestAsync(new DeleteUserRequest { Id = 2 });
        Console.WriteLine($"  {deleted.Status} -> {ExampleConsole.Pretty(deleted.Data)}");

        try
        {
            await client.RequestAsync(new GetMissingUserRequest { Id = 999 });
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  HTTP 404 -> {ex.Code}: {ex.Message}");
            // 错误响应整体可访问（Status / Headers / Data），ToJson 给出可直接落日志的结构化快照
            var snapshot = ex.ToJson();
            Console.WriteLine($"  ToJson = method={snapshot["method"]} url={snapshot["url"]} status={snapshot["status"]}");
        }
    }
}
