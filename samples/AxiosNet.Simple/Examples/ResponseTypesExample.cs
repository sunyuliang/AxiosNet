using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>9. 其他 responseType：text / arraybuffer。</summary>
public sealed class ResponseTypesExample : IExample
{
    /// <summary>本示例专用客户端（无 BaseAddress，地址写全）：首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 9;

    public string Title => "responseType：text / arraybuffer";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient();

        var textResponse = await http.GetAsync(context.BaseUrl, c => c.ResponseType = ResponseType.Text);
        Console.WriteLine($"  text: {textResponse.Data}");

        var bytesResponse = await http.PostAsync<byte[]>(context.BaseUrl + "echo", new byte[] { 1, 2, 3, 250, 255 }, c => c.ResponseType = ResponseType.ArrayBuffer);
        Console.WriteLine($"  arraybuffer 回显: {string.Join(",", bytesResponse.Data!)}");
    }
}
