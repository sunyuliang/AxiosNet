using System.Security.Cryptography;
using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 18. 单文件上传的两种形态：裸流直传（请求体就是文件字节）与 multipart 单文件。
/// 演示服务器用 MultipartReader 按字节精确解析，回报文件名/字段名/字节数/SHA256。
/// </summary>
public sealed class SingleFileUploadExample : IExample
{
    public int Number => 18;

    public string Title => "单文件上传：裸流直传 / multipart（服务端精确解析）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var path = Path.Combine(Path.GetTempPath(), "axios-net-single-file.bin");
        var bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(bytes);   // 含 0x00 / CRLF，用于验证服务端按字节截取不会截断或污染
        await File.WriteAllBytesAsync(path, bytes);

        var localHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        try
        {
            using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

            // 1) 裸流直传：请求体 = 文件字节，Content-Type 声明文件类型，文件名走自定义头
            await using (var stream = File.OpenRead(path))
            {
                var raw = await http.PostAsync<JsonElement>("upload/raw", stream, c =>
                {
                    c.ContentType = "application/octet-stream";
                    c.Headers.Set("X-File-Name", "single-file.bin");
                });

                var data = raw.Data;
                Console.WriteLine(
                    $"  裸流直传: 服务端收到 fileName={data.GetProperty("fileName").GetString()}, "
                    + $"bytes={data.GetProperty("bytes").GetInt64()}, "
                    + $"contentType={data.GetProperty("contentType").GetString()}");
                Console.WriteLine($"    SHA256 {Verify(data.GetProperty("sha256").GetString(), localHash)}（本地 {localHash[..12]}…）");
            }

            // 2) multipart 单文件：PostFormAsync 默认 multipart，字段名 file 与服务端解析一致
            var form = await http.PostFormAsync<JsonElement>("upload/single", new
            {
                file = FormFile.FromPath(path, "single-file.bin", "application/octet-stream"),
                note = "季度报告"
            });

            var multipart = form.Data;
            Console.WriteLine(
                $"  multipart: field={multipart.GetProperty("field").GetString()}, "
                + $"fileName={multipart.GetProperty("fileName").GetString()}, "
                + $"bytes={multipart.GetProperty("bytes").GetInt64()}, "
                + $"note={multipart.GetProperty("note").GetString()}");
            Console.WriteLine(
                $"    SHA256 {Verify(multipart.GetProperty("sha256").GetString(), localHash)}"
                + $"，本地 {bytes.Length} bytes -> 服务端 {multipart.GetProperty("bytes").GetInt64()} bytes");

            Console.WriteLine("    要点：裸流适合大文件（无边界开销），multipart 适合文件 + 其它字段");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Verify(string? serverHash, string localHash)
        => serverHash == localHash ? "一致" : $"不一致（server={serverHash}）";
}
