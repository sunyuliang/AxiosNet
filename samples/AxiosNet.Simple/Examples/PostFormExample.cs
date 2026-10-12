using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 4. POST 字典 -> application/x-www-form-urlencoded。
/// </summary>
public sealed class PostFormExample : IExample
{
    public int Number => 4;

    public string Title => "POST 字典自动表单编码";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });
        var form = new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        };

        var response = await http.PostAsync<JsonElement>("/form", form);
        Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
    }
}
