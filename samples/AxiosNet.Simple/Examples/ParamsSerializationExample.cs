using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>5. params 序列化（数组 / 嵌套对象 / 已有 query 拼接）。</summary>
public sealed class ParamsSerializationExample : IExample
{
    public int Number => 5;

    public string Title => "params 序列化（数组 a[]=1&a[]=2、嵌套 o[k]=v）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var response = await Axios.GetAsync<JsonElement>(context.BaseUrl + "search?fixed=1", c =>
            c.Params = new
            {
                q = "hello world",
                ids = new[] { 1, 2 },
                filter = new { kind = "book" }
            });

        Console.WriteLine($"  {response.Status} -> 服务器收到的 query: {response.Data.GetProperty("query").GetString()}");
    }
}
