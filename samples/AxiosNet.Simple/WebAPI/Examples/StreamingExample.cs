using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 8. 流式响应：SSE 与 JSON 数组都逐帧解析成 <see cref="IAsyncEnumerable{T}"/>——
/// 状态码与响应头在 <c>await</c> 时就有，帧在枚举时才读，连接在枚举结束时归还。
/// 两种格式共用同一套字节级分帧与空闲超时，差别只在「帧边界怎么找」。
/// </summary>
public sealed class StreamingExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 8;

    public string Title => "流式响应（SSE / JSON 数组）：RequestStreamAsync + IAsyncEnumerable 逐帧消费";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var client = _client ??= new ApiClient(context.BaseUrl);

        await RunSseAsync(client);
        await RunJsonArrayAsync(client);
    }

    /// <summary>SSE：空行结束一帧，<c>data: [DONE]</c> 表示流结束。</summary>
    private static async Task RunSseAsync(ApiClient client)
    {
        // 默认（不传格式）按 NDJSON 解析；这里是 SSE，显式指定
        var response = await client.RequestStreamAsync(
            new StreamEventsRequest(),
            AxiosStreamFormat.ServerSentEvents);

        // await 已经完成：状态码与响应头到手，正文还没读（状态码错误会在这一行抛出）
        Console.WriteLine($"  响应头已到达：HTTP {response.Status} / {response.GetHeader("Content-Type")}");
        Console.WriteLine($"  超时口径：Timeout 只覆盖到响应头，正文按 StreamIdleTimeout={client.StreamIdleTimeout}（Zero = 不限）");

        var started = DateTime.UtcNow;
        await foreach (var chunk in response.Data!)
        {
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Console.WriteLine($"  <- 第 {chunk.Index} 帧 {chunk.Text}（+{elapsed:0}ms）");
        }

        Console.WriteLine("  流结束（收到 data: [DONE] 或 EOF），连接已随枚举器释放");
    }

    /// <summary>
    /// JSON 数组：批量 / 分页响应最常见的形态。帧边界由 <c>Utf8JsonReader</c> 在字节层增量定位，
    /// 元素收尾即交付（不必等收尾的 <c>]</c>），字符串里的 <c>,</c> / <c>]</c> 与跨块的多字节字符都不影响。
    /// </summary>
    private static async Task RunJsonArrayAsync(ApiClient client)
    {
        Console.WriteLine();
        Console.WriteLine("  —— 换一个端点，正文是 JSON 数组（[{...},{...}]）——");

        var response = await client.RequestStreamAsync(
            new StreamPagesRequest(),
            AxiosStreamFormat.JsonArray);

        Console.WriteLine($"  响应头已到达：HTTP {response.Status} / {response.GetHeader("Content-Type")}");

        var started = DateTime.UtcNow;
        await foreach (var page in response.Data!)
        {
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Console.WriteLine($"  <- 第 {page.Index} 页 {page.Text}（+{elapsed:0}ms）");
        }

        Console.WriteLine("  收到收尾的 ']'：枚举结束，连接已随枚举器释放");
    }
}
