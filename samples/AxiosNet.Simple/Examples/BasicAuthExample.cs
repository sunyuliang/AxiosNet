using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 6. HTTP Basic 认证。
/// </summary>
public sealed class BasicAuthExample : IExample
{
    public int Number => 6;

    public string Title => "config.auth 发送 HTTP Basic 认证";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient();

        var response = await http.GetAsync<JsonElement>(context.BaseUrl + "secure", c
            => c.Connection.Auth = new AxiosAuth("user", "pass"));

        Console.WriteLine($"  {response.Status} -> authenticated={response.Data.GetProperty("authenticated").GetBoolean()}");
        Console.WriteLine($"  Authorization = {response.Data.GetProperty("authorization").GetString()}");
    }
}
