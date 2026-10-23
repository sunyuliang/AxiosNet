using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 8. 流式响应：SSE 逐帧解析成 <see cref="IAsyncEnumerable{T}"/>——
/// 状态码与响应头在 <c>await</c> 时就有，帧在枚举时才读，连接在枚举结束时归还。
/// </summary>
public sealed class StreamingExample : IExample
{
    public int Number => 8;

    public string Title => "流式响应（SSE）：RequestStreamAsync + IAsyncEnumerable 逐帧消费";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        using var client = new ApiClient(context.BaseUrl);

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
}
