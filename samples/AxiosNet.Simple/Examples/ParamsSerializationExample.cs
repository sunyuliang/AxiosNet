using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>5. params 序列化（数组 / 嵌套对象 / 已有 query 拼接）。</summary>
public sealed class ParamsSerializationExample : IExample
{
    /// <summary>本示例专用客户端（无 BaseAddress，地址写全）：首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 5;

    public string Title => "params 序列化（数组 a[]=1&a[]=2、嵌套 o[k]=v）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient();

        var response = await http.GetAsync<JsonElement>(context.BaseUrl + "search?fixed=1", c =>
            c.Params = new
            {
                q = "hello world",
                ids = new[] { 1, 2 },
                filter = new { kind = "book" }
            });

        Console.WriteLine($"  {response.Status} -> 服务器收到的 query: {response.Data.GetProperty("query").GetString()}");
    }
}
