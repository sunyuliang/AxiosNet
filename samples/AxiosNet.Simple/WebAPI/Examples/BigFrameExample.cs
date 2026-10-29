using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 10. 单帧上限：默认 8 MB 会拒掉 9 MB 的单个元素，把 <c>Limits.MaxStreamFrameLength</c>
/// 放大到 12 MB 后同一个端点可以正常逐帧读完。
/// 上限只是天花板，不按上限预分配；要处理更大的正文应改用 <c>ResponseType.Stream</c> 自己分块。
/// </summary>
public sealed class BigFrameExample : IExample
{
    /// <summary>默认配置的客户端（8 MB 上限）：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _strict;

    /// <summary>放开到 12 MB 的客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _relaxed;

    public int Number => 10;

    public string Title => "单帧上限：Limits.MaxStreamFrameLength（默认 8 MB / 放开到 12 MB）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 1) 默认 8 MB：单个元素 9 MB，超过上限 -> ERR_BAD_RESPONSE（连帧都交付不出来）
        var strict = _strict ??= new ApiClient(context.BaseUrl);
        try
        {
            var response = await strict.RequestStreamAsync(new StreamBigFrameRequest(), AxiosStreamFormat.JsonArray);

            await foreach (var chunk in response.Data!)
            {
                Console.WriteLine($"  [意外] 9 MB 的单帧被放行，长度 {chunk.Text.Length:N0}");
            }
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  默认 8 MB 上限拦下 9 MB 帧：code={ex.Code}");
            Console.WriteLine($"  {ex.Message}");
        }

        // 2) 放开到 12 MB：同一端点可以逐帧读完（这里只打印长度，不把 9 MB 正文打到控制台）
        var relaxed = _relaxed ??= new ApiClient(
            context.BaseUrl,
            defaults => defaults.Limits.MaxStreamFrameLength = 12 * 1024 * 1024);

        var relaxedResponse = await relaxed.RequestStreamAsync(new StreamBigFrameRequest(), AxiosStreamFormat.JsonArray);

        await foreach (var chunk in relaxedResponse.Data!)
        {
            Console.WriteLine($"  12 MB 上限下读到大帧：index={chunk.Index}，text 长度 {chunk.Text.Length:N0} 字符");
        }
    }
}
