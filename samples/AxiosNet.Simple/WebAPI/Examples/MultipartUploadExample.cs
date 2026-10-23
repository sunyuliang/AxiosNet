using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 9. multipart 上传：<c>[Multipart]</c> 让模型自身即表单，<c>[Part]</c> 覆盖字段名 / 文件名 / Content-Type；
/// 服务器按字节精确解析并回报部件清单，装配完全复用核心包的 multipart 通道（boundary / 所有权规则不变）。
/// </summary>
public sealed class MultipartUploadExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 9;

    public string Title => "multipart 上传：[Multipart] + [Part]（模型自身即表单，服务器按字节解析回执）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var client = _client ??= new ApiClient(context.BaseUrl);

        var response = await client.RequestAsync(new MultipartUploadRequest
        {
            Title = "年度报告",
            Blob = "二进制内容（含中文与换行）\r\nsecond line"u8.ToArray(),
            Tags = new Dictionary<string, string> { ["env"] = "sample" }
        });

        Console.WriteLine($"  上传完成：{response.Data!.PartCount} 个部件，Title = {response.Data.Title}");
        foreach (var part in response.Data.Parts)
        {
            Console.WriteLine(
                $"  <- {part.Name}  filename={part.FileName ?? "-"}  type={part.ContentType ?? "-"}  {part.Length}B");
        }
    }
}
