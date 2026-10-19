using System.Text;
using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 13. multipart/form-data：普通字段 + 一堆文件（内存文件与本地文件各一）。
/// 演示服务器用 MultipartReader 按字节精确解析，回报每个部件的字段名/文件名/字节数/SHA256。
/// </summary>
public sealed class MultipartExample : IExample
{
    public int Number => 13;

    public string Title => "multipart/form-data：字段 + 一堆文件（服务端精确解析）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var tempPath = Path.Combine(Path.GetTempPath(), "axios-net-upload-demo.txt");
        await File.WriteAllTextAsync(tempPath, "from-disk");

        try
        {
            using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

            var response = await http.PostAsync<JsonElement>(
                "upload",
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

            var data = response.Data;
            Console.WriteLine(
                $"  {response.Status} -> 部件 {data.GetProperty("parts").GetInt32()} 个, "
                + $"文件 {data.GetProperty("files").GetInt32()} 个, "
                + $"文件总字节 {data.GetProperty("bytes").GetInt64()}");

            foreach (var part in data.GetProperty("partsDetail").EnumerateArray())
            {
                var field = part.GetProperty("field").GetString();
                var fileName = part.TryGetProperty("fileName", out var file) && file.ValueKind == JsonValueKind.String
                    ? file.GetString()
                    : null;
                var bytes = part.GetProperty("bytes").GetInt64();
                var sha = part.GetProperty("sha256").GetString()!;

                Console.WriteLine(fileName is null
                    ? $"    [{field}] 普通字段, {bytes} bytes"
                    : $"    [{field}] 文件 {fileName}, {bytes} bytes, sha256={sha[..12]}…");
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
