using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 4. POST 字典 + 显式 <c>application/x-www-form-urlencoded</c>：编码方式由 config.ContentType 决定。
/// </summary>
public sealed class PostFormExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 4;

    public string Title => "POST 字典表单（显式 ContentType）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });
        var form = new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        };

        // 默认是 AxiosContentType.Json（字典同样按 JSON 发送）；需要表单编码必须显式声明
        var response = await http.PostAsync<JsonElement>(
            "/form",
            form,             
            c => c.ContentType = AxiosContentType.FormUrlEncoded);

        Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
    }
}
