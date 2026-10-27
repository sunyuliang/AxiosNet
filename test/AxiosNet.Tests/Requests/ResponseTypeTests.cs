
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应类型与下载：text / JsonElement / dynamic / byte[] / Stream、下载落盘。
/// </summary>
public sealed class ResponseTypeTests
{
    [Fact]
    public async Task GetAsync_TypedJson_ReturnsFullAxiosResponseShape()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(
                Headers: new Dictionary<string, string> { ["X-Greeting"] = "hello" },
                Body: TestJson.Bytes(new Person("alice", 18))));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "users/1");

        Assert.Equal(200, response.Status);
        Assert.Equal("OK", response.StatusText);
        Assert.NotNull(response.Data);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal(18, response.Data.Age);
        Assert.Equal("hello", response.GetHeader("X-Greeting"));
        Assert.NotNull(response.Config);
        Assert.NotNull(response.Request);
        Assert.Equal(HttpMethod.Get, response.Request!.Method);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/1", received.Path);
    }

    [Fact]
    public async Task GetAsync_NonGeneric_JsonDataIsJsonElement()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + "people/1");

        var element = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal("alice", element.GetProperty("name").GetString());
        Assert.Equal(18, element.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task ResponseType_Text_ReturnsString()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("pong"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("pong", Assert.IsType<string>(response.Data));
    }

    [Fact]
    public async Task ResponseType_Stream_ReturnsReadableStream()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("stream-body"), ContentType: "text/plain"));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Stream);

        var stream = Assert.IsAssignableFrom<Stream>(response.Data);
        using var reader = new StreamReader(stream);
        Assert.Equal("stream-body", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task PostAsync_Bytes_ResponseTypeArrayBuffer_RoundTrips()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/octet-stream"));

        using var client = new AxiosClient();
        var payload = new byte[] { 0, 1, 2, 250, 255 };

        var response = await client.PostAsync<byte[]>(
            server.Url + "bytes",
            payload,
            c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, response.Data);
        Assert.Equal(payload, server.Received[0].Body);
    }

    [Fact]
    public async Task ResponseType_ArrayBuffer_NonGeneric_ReturnsBytes()
    {
        var payload = new byte[] { 1, 2, 3, 254, 255 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, Assert.IsType<byte[]>(response.Data));
    }

    [Fact]
    public async Task GetAsync_OfString_PlainText_ReturnsRawText()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("hello world"), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "ping");

        Assert.Equal("hello world", response.Data);
    }

    [Fact]
    public async Task GetAsync_OfString_JsonString_ReturnsRawText()
    {
        // 原始内容优先：即使是 JSON 字符串也原样返回（含引号），不再自动剥离
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("\"hello\""), ContentType: "application/json; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url);

        Assert.Equal("\"hello\"", response.Data);
    }

    [Fact]
    public async Task InstanceDefaults_ResponseTypeDynamic_IsRespectedUnlessOverridden()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            ResponseType = ResponseType.Dynamic
        });

        // 快捷方法/新建 config 都没显式指定 responseType，应沿用实例默认。
        Assert.IsType<AxiosDynamic>((await client.GetAsync("any")).Data);
        Assert.IsType<AxiosDynamic>((await client.RequestAsync(c => c.Url = "any")).Data);
        Assert.IsType<AxiosDynamic>((await client.RequestAsync(new AxiosConfig { Url = "any" })).Data);

        // 显式指定时仍然按其覆盖
        Assert.IsType<string>((await client.GetAsync("any", c => c.ResponseType = ResponseType.Text)).Data);
    }

    // ---------------------------------------------------------------
    // 空正文 / HEAD 的流式响应
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ZeroLengthBody_CompletesImmediately()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Array.Empty<byte>(), ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url);
        await using var stream = response.Data!;

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(0, buffer.Length);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task HeadRequest_WithStreamResponseType_ReturnsEmptyStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: TestJson.Bytes(new { ok = true }),
            ContentType: "application/json"));
        using var client = new AxiosClient();

        var response = await client.HeadAsync<Stream>(server.Url);
        await using var stream = response.Data!;

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(0, buffer.Length);
        Assert.Equal("HEAD", server.Received[0].Method);
    }

    /// <summary>服务端不声明 Content-Length、以连接关闭界定正文时，缓冲读取返回的字节数必须与实际一致。</summary>
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

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n"));
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

        // 同步 Read 也要读到完整正文。
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

        // 关键：流读完后连接必须能回到池里复用。
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
        // 服务端必须「自己不回应」，延迟远大于调度抖动幅度即可。
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

    // 泛型响应的 Data 可重复读取

    [Fact]
    public async Task TypedResponse_Data_CanBeReadRepeatedly()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("hello"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "text", c => c.ResponseType = ResponseType.Text);

        Assert.Equal("hello", response.Data);
        Assert.Equal("hello", response.Data);
    }

    // ------------------------------------------------------------------
    // L16：流式响应的 WriteAsync 走异步直连（不退化成同步等待）
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_WriteAsync_ThrowsNotSupported_WithoutHanging()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        var stream = (Stream)response.Data!;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            stream.WriteAsync(new byte[] { 1 }, 0, 1));

        stream.Dispose();
    }

    [Fact]
    public async Task ResponseHeaders_AreExposedCaseInsensitively()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Headers: new Dictionary<string, string> { ["X-Multi"] = "one" }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        Assert.Equal("one", response.GetHeader("x-multi"));
        Assert.True(response.Headers.ContainsKey("X-MULTI"));
    }

    private static readonly byte[] Payload = Enumerable.Range(0, 4096)
        .Select(i => (byte)(i % 251))
        .ToArray();

    /// <summary>服务端「永不自行回应」的用例预算：实现真不超时，用例以 <see cref="TimeoutException"/> 失败而不是挂住 CI。</summary>
    private static readonly TimeSpan HungResponseBudget = TimeSpan.FromSeconds(10);

    private static TestResponse Binary(TestRequest _)
        => new(Body: Payload, ContentType: "application/octet-stream");


    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}