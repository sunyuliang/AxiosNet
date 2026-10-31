using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>4. POST 字典 + 显式 <c>application/x-www-form-urlencoded</c>。</summary>
public sealed class PostFormExample : IExample
{
    public int Number => 4;

    public string Title => "POST 字典表单（显式 ContentType）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 没有实例配置：直接用全局默认实例发请求（连接池进程内复用）
        var form = new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        };

        // 默认是 AxiosContentType.Json（字典同样按 JSON 发送）；需要表单编码必须显式声明
        var response = await Axios.PostAsync<JsonElement>(
            context.BaseUrl + "form", form, c => c.ContentType = AxiosContentType.FormUrlEncoded);

        Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
    }
}
