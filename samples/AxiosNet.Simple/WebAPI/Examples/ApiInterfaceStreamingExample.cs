using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 14. 接口级流式：返回 <c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;</c> 即流式方法，
/// <c>[Stream(format)]</c> 选择帧格式（NDJSON 为默认）。状态码 / 响应头在 await 时到手，帧在枚举时才读。
/// </summary>
public sealed class ApiInterfaceStreamingExample : IExample
{
    /// <summary>接口代理客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 14;

    public string Title => "接口级流式：返回 IAsyncEnumerable + [Stream] 选帧格式（SSE / JSON 数组）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl);
        var api = client.Create<IStreamApi>();

        await RunSseAsync(api);
        await RunJsonArrayAsync(api);
    }

    /// <summary>SSE：<c>[Stream(AxiosStreamFormat.ServerSentEvents)]</c> 逐帧交付。</summary>
    private static async Task RunSseAsync(IStreamApi api)
    {
        var response = await api.EventsAsync();

        Console.WriteLine($"  响应头已到达：HTTP {response.Status} / {response.GetHeader("Content-Type")}");

        await foreach (var chunk in response.Data!)
        {
            Console.WriteLine($"  <- 第 {chunk.Index} 帧 {chunk.Text}");
        }

        Console.WriteLine("  流结束（收到 data: [DONE] 或 EOF），连接已随枚举器释放");
    }

    /// <summary>JSON 数组：<c>[Stream(AxiosStreamFormat.JsonArray)]</c> 按顶层元素逐帧交付。</summary>
    private static async Task RunJsonArrayAsync(IStreamApi api)
    {
        Console.WriteLine();
        Console.WriteLine("  —— 换一个端点，正文是 JSON 数组（[{...},{...}]）——");

        var response = await api.PagesAsync();

        Console.WriteLine($"  响应头已到达：HTTP {response.Status} / {response.GetHeader("Content-Type")}");

        await foreach (var page in response.Data!)
        {
            Console.WriteLine($"  <- 第 {page.Index} 页 {page.Text}");
        }

        Console.WriteLine("  收到收尾的 ']'：枚举结束，连接已随枚举器释放");
    }
}

/// <summary>接口级流式样例：返回 IAsyncEnumerable 即流式，[Stream] 选帧格式。</summary>
public interface IStreamApi
{
    [Get("api/stream/events")]
    [Stream(AxiosStreamFormat.ServerSentEvents)]
    Task<AxiosResponse<IAsyncEnumerable<StreamChunk>>> EventsAsync(CancellationToken cancellationToken = default);

    [Get("api/stream/pages")]
    [Stream(AxiosStreamFormat.JsonArray)]
    Task<AxiosResponse<IAsyncEnumerable<StreamChunk>>> PagesAsync(CancellationToken cancellationToken = default);
}
