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

        var response = await Axios.GetAsync<JsonElement>("secure", c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Connection.Auth = new AxiosAuth("user", "pass");
        });

        Console.WriteLine($"  {response.Status} -> authenticated={response.Data.GetProperty("authenticated").GetBoolean()}");
        Console.WriteLine($"  Authorization = {response.Data.GetProperty("authorization").GetString()}");
    }
}
