using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 13. 接口级 multipart：方法上的 <c>[Multipart]</c> + 参数上的 <c>[Part]</c> —— 参数即表单。
/// 装配与请求模型共用同一套内核：字段名、文件名、Content-Type 与流所有权规则完全一致。
/// </summary>
public sealed class ApiInterfaceMultipartExample : IExample
{
    /// <summary>接口代理客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 13;

    public string Title => "接口级 multipart：[Multipart] + 参数 [Part]（参数即表单，服务器按字节解析回执）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl);
        var api = client.Create<IUploadApi>();

        var response = await api.UploadAsync(
            "年度报告",
            "二进制内容（接口级）\r\nsecond line"u8.ToArray(),
            new Dictionary<string, string> { ["env"] = "interface" });

        Console.WriteLine($"  上传完成：{response.Data!.PartCount} 个部件，Title = {response.Data.Title}");
        foreach (var part in response.Data.Parts)
        {
            Console.WriteLine(
                $"  <- {part.Name}  filename={part.FileName ?? "-"}  type={part.ContentType ?? "-"}  {part.Length}B");
        }
    }
}

/// <summary>接口级 multipart 样例：方法声明 [Multipart]，参数即部件。</summary>
public interface IUploadApi
{
    [Post("api/upload")]
    [Multipart]
    Task<AxiosResponse<UploadResult>> UploadAsync(
        [Part("Title")] string title,
        [Part(FileName = "data.bin")] byte[] blob,
        [Part("meta")] Dictionary<string, string> tags,
        CancellationToken cancellationToken = default);
}
