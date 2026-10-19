using System.Text;
using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 13. multipart/form-data：普通字段 + 一堆文件（内存文件与本地文件各一）。
/// </summary>
public sealed class MultipartExample : IExample
{
    public int Number => 13;

    public string Title => "multipart/form-data：字段 + 一堆文件";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var tempPath = Path.Combine(Path.GetTempPath(), "axios-net-upload-demo.txt");
        await File.WriteAllTextAsync(tempPath, "from-disk");

        try
        {
            using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

            var response = await http.PostAsync<JsonElement>(
                "/upload",
                new
                {
                    title = "季度报告",
                    files = new[]
                    {
                        // 内存中的文件：文件名 + 字节内容 + 部件 Content-Type
                        new FormFile("readme.txt", Encoding.UTF8.GetBytes("in-memory"), "text/plain"),
                        // 本地文件：库负责打开流，请求结束后随请求一起释放
                        FormFile.FromPath(tempPath, "from-disk.txt", "text/plain")
                    }
                },
                c => c.ContentType = "multipart/form-data");

            Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
