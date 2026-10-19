using System.Net.Http.Headers;
using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 文件下载的两种形态：
/// - 流式（ResponseType.Stream / GetAsync&lt;Stream&gt;）：内存占用恒定，适合大文件；
/// - 缓冲（ResponseType.ArrayBuffer）：一次性拿到 byte[]，适合小文件。
/// 另外验证流读完后连接能回到池里复用（下载场景最容易踩的坑）。
/// </summary>
public sealed class DownloadTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 4096)
        .Select(i => (byte)(i % 251))
        .ToArray();

    private static TestResponse Binary(TestRequest _)
        => new(Body: Payload, ContentType: "application/octet-stream");

    [Fact]
    public async Task GetAsyncOfStream_StreamsToLocalFile()
    {
        await using var server = TestHttpServer.Start(Binary);
        using var client = new AxiosClient();

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var response = await client.GetAsync<Stream>(server.Url + "files/report.bin");

            Assert.Equal(200, response.Status);
            Assert.IsAssignableFrom<Stream>(response.Data);

            await using (var stream = response.Data!)
            await using (var file = File.Create(path))
            {
                await stream.CopyToAsync(file);
            }

            Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResponseTypeStream_WithExplicitConfig_AlsoWorks()
    {
        await using var server = TestHttpServer.Start(Binary);
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "files/report.bin", c => c.ResponseType = ResponseType.Stream);

        await using var stream = Assert.IsAssignableFrom<Stream>(response.Data);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(Payload, buffer.ToArray());
    }

    [Fact]
    public async Task ResponseTypeArrayBuffer_ReturnsWholeBytes()
    {
        await using var server = TestHttpServer.Start(Binary);
        using var client = new AxiosClient();

        var response = await client.GetAsync<byte[]>(server.Url + "files/small.bin");
        Assert.Equal(Payload, response.Data);

        // 或非泛型 + ArrayBuffer，Data 也是 byte[]
        var raw = await client.GetAsync(server.Url + "files/small.bin", c => c.ResponseType = ResponseType.ArrayBuffer);
        Assert.Equal(Payload, Assert.IsType<byte[]>(raw.Data));
    }

    [Fact]
    public async Task StreamDownload_DisposedStream_ReturnsConnectionToPool()
    {
        await using var server = TestHttpServer.Start(Binary);
        using var client = new AxiosClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync<Stream>(server.Url + "files/report.bin");
            await using var stream = response.Data!;

            // 边读边丢，模拟落盘/转发
            await stream.CopyToAsync(Stream.Null);
        }

        // 关键：流读完后连接必须能回到池里复用，否则下载会把连接池打满
        Assert.Equal(1, server.TotalConnections);
        Assert.Equal(3, server.RequestsPerConnection.Values.Single());
    }

    [Fact]
    public async Task ContentDisposition_FileNameCanBeReadFromHeaders()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Payload,
            ContentType: "application/octet-stream",
            Headers: new Dictionary<string, string>
            {
                ["Content-Disposition"] = "attachment; filename=\"report 2024.pdf\"; filename*=UTF-8''report%202024.pdf"
            }));

        using var client = new AxiosClient();
        var response = await client.GetAsync<Stream>(server.Url + "files/1");

        var disposition = ContentDispositionHeaderValue.Parse(response.GetHeader("Content-Disposition")!);
        var fileName = (disposition.FileNameStar ?? disposition.FileName ?? string.Empty).Trim('"');

        Assert.Equal("report 2024.pdf", fileName);

        await using var stream = response.Data!;
        await stream.CopyToAsync(Stream.Null);
    }

    [Fact]
    public async Task Download_WithTimeoutAndCancellation_DoesNotHang()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(500);
            return new TestResponse(Body: Payload, ContentType: "application/octet-stream");
        });

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Stream>(
            server.Url + "files/slow.bin",
            c =>
            {
                c.ResponseType = ResponseType.Stream;
                c.Timeout = TimeSpan.FromMilliseconds(150);
            }));

        Assert.Equal("ECONNABORTED", ex.Code);
    }
}
