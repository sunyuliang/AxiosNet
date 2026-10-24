using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

    /// <summary>
    /// 服务端「永不自行回应」的用例预算：断言只需证明超时确实生效（否则请求会一直等下去），
    /// 不必精确到几百毫秒。
    /// <para>
    /// 超时计时器的回调同样要排进线程池，CI 满载时可能晚到数秒（实测 GitHub Actions 上约 2~3s，
    /// 见 <c>StreamCancellationTests</c> 里同名注释）。因此这里的预算只当「测试自己的兜底」：
    /// 实现真不超时，用例以 <see cref="TimeoutException"/> 失败，而不是把整条 CI 挂住。
    /// </para>
    /// </summary>
    private static readonly TimeSpan HungResponseBudget = TimeSpan.FromSeconds(10);

    private static TestResponse Binary(TestRequest _)
        => new(Body: Payload, ContentType: "application/octet-stream");

    /// <summary>
    /// 服务端不声明 Content-Length、以连接关闭 delimiting 正文的响应（TestHttpServer 发不出这种形态）。
    /// 覆盖缓冲读取的「未知长度」分支：边读边扩容，返回数组长度必须恒等于实际读到的字节。
    /// </summary>
    [Fact]
    public async Task BufferedRead_WithoutDeclaredContentLength_ReturnsExactBytes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var expected = Encoding.UTF8.GetBytes("no-content-length-body");
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            using var stream = new NetworkStream(socket, ownsSocket: true);

            // 读掉请求头（读到 \r\n\r\n 即可，不关心内容）
            var buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(buffer).Contains("\r\n\r\n"))
            {
                if (await stream.ReadAsync(buffer) == 0)
                {
                    break;
                }
            }

            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(expected);
            await stream.FlushAsync();
        });

        try
        {
            using var client = new AxiosClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var response = await client.GetAsync<byte[]>($"http://127.0.0.1:{port}/x", cancellationToken: cts.Token);

            Assert.Equal(200, response.Status);
            Assert.Equal(expected, response.Data);

            await serverTask.WaitAsync(cts.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

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
    public async Task ResponseTypeStream_SyncRead_ReturnsWholePayload()
    {
        await using var server = TestHttpServer.Start(Binary);
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "files/report.bin");
        await using var stream = response.Data!;

        // 同步 Read 内部是「带令牌的异步核心 + 阻塞等待」，正文仍然要一个字节不差
        using var buffer = new MemoryStream();
        var chunk = new byte[512];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

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
        // 服务端必须「自己不回应」：只延迟 500ms 的话，CI 上被推迟的超时回调会输给这条应答，
        // 请求成功返回 —— 断言看到的就是「没有抛异常」。延迟只要远大于调度抖动幅度即可。
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return new TestResponse(Body: Payload, ContentType: "application/octet-stream");
        });

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Stream>(
            server.Url + "files/slow.bin",
            c =>
            {
                c.ResponseType = ResponseType.Stream;
                c.Timeout = TimeSpan.FromMilliseconds(150);
            }).WaitAsync(HungResponseBudget));

        Assert.Equal("ECONNABORTED", ex.Code);
    }
}
