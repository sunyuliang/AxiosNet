using System.Net.Http.Headers;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 14. 文件下载：流式落盘（内存占用恒定）、一次性字节，以及用 Content-Disposition 取文件名。
/// </summary>
public sealed class DownloadExample : IExample
{
    /// <summary>本示例专用客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 14;

    public string Title => "文件下载：GetAsync<Stream> 流式落盘 / GetAsync<byte[]>";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        // 1) 大文件：<Stream> 自动切到 ResponseType.Stream，响应头一到就返回，边读边写盘
        var streamResponse = await http.GetAsync<Stream>("download");

        var fileName = ResolveFileName(streamResponse.GetHeader("Content-Disposition")) ?? "download.bin";
        var targetPath = Path.Combine(Path.GetTempPath(), "axios-net-" + fileName);

        long total = 0;
        await using (var source = streamResponse.Data!)
        await using (var target = File.Create(targetPath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                total += read;
            }
        }

        Console.WriteLine($"  流式下载: {fileName} -> {targetPath}");
        Console.WriteLine($"  字节数: {total}, status={streamResponse.Status}, content-type={streamResponse.GetHeader("Content-Type")}");

        File.Delete(targetPath);

        // 2) 小文件：<byte[]> 一次性拿到全部字节（整包进内存，仅适合小文件）
        var bytesResponse = await http.GetAsync<byte[]>("download");
        Console.WriteLine($"  缓冲下载: {bytesResponse.Data!.Length} 字节（Data 类型 = {bytesResponse.Data.GetType().Name}）");
    }

    /// <summary>从 <c>Content-Disposition: attachment; filename=...</c> 里取服务端建议的文件名。</summary>
    private static string? ResolveFileName(string? contentDisposition)
    {
        if (string.IsNullOrWhiteSpace(contentDisposition))
        {
            return null;
        }

        var disposition = ContentDispositionHeaderValue.Parse(contentDisposition);
        return (disposition.FileNameStar ?? disposition.FileName)?.Trim('"');
    }
}
