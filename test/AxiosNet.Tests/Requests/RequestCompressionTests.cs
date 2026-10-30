using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System.IO.Compression;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 请求体压缩：库缓冲出来的正文（文本 / 二进制 / 表单）按 <c>RequestCompression</c> 压缩并补 <c>Content-Encoding</c>；
/// 流式正文、自带 <see cref="HttpContent"/>、multipart 与手写 Content-Encoding 一律拒绝。
/// </summary>
public sealed class RequestCompressionTests
{
    [Theory]
    [InlineData(AxiosCompression.Gzip, "gzip")]
    [InlineData(AxiosCompression.Deflate, "deflate")]
    [InlineData(AxiosCompression.Brotli, "br")]
    public async Task StructuredBody_IsCompressed_AndDecodesBackToJson(AxiosCompression compression, string headerValue)
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "users",
            new Person("alice", 42),
            c => c.RequestCompression = compression);

        var received = server.Received[0];

        Assert.Equal(headerValue, received.Headers["content-encoding"]);
        Assert.Equal("""{"name":"alice","age":42}""", Encoding.UTF8.GetString(Decompress(received.Body, compression)));

        // 压缩后长度已知，Content-Length 与实际字节数一致。
        Assert.Equal(received.Body.Length.ToString(), received.Headers["content-length"]);
    }

    [Fact]
    public async Task FormBody_IsCompressed()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "login",
            new Dictionary<string, string> { ["user"] = "alice", ["role"] = "admin" },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.RequestCompression = AxiosCompression.Gzip;
            });

        var received = server.Received[0];

        Assert.Equal("gzip", received.Headers["content-encoding"]);
        Assert.Equal("user=alice&role=admin", Encoding.ASCII.GetString(Decompress(received.Body, AxiosCompression.Gzip)));
    }

    [Fact]
    public async Task ByteArrayBody_IsCompressed()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "blob",
            new byte[] { 1, 2, 3, 4 },
            c => c.RequestCompression = AxiosCompression.Gzip);

        Assert.Equal("gzip", server.Received[0].Headers["content-encoding"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Decompress(server.Received[0].Body, AxiosCompression.Gzip));
    }

    [Fact]
    public async Task NotEnabled_LeavesBodyUntouched()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "users", new Person("alice", 42));

        Assert.False(server.Received[0].Headers.ContainsKey("content-encoding"));
    }

    [Fact]
    public async Task InstanceDefault_CanBeDisabledPerRequest()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { RequestCompression = AxiosCompression.Gzip });

        await client.PostAsync(server.Url + "a", new Person("alice", 42));
        await client.PostAsync(server.Url + "b", new Person("alice", 42), c => c.RequestCompression = AxiosCompression.None);

        Assert.Equal("gzip", server.Received[0].Headers["content-encoding"]);
        Assert.False(server.Received[1].Headers.ContainsKey("content-encoding"));
    }

    [Fact]
    public async Task MaxBodyLength_IsMeasuredOnTheCompressedBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 8 KB 同字符正文压缩后远小于 1 KB：按压缩后长度校验，请求能发出去。
        var data = new { text = new string('a', 8 * 1024) };
        await client.PostAsync(
            server.Url + "x",
            data,
            c =>
            {
                c.RequestCompression = AxiosCompression.Gzip;
                c.Limits.MaxBodyLength = 1024;
            });

        Assert.Single(server.Received);
        Assert.True(server.Received[0].Body.Length <= 1024);
    }

    [Fact]
    public async Task StreamBody_IsRejected()
    {
        using var client = new AxiosClient();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/upload",
            stream,
            c => c.RequestCompression = AxiosCompression.Gzip));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("Stream", ex.Message);
    }

    [Fact]
    public async Task CallerSuppliedHttpContent_IsRejected()
    {
        using var client = new AxiosClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/upload",
            content,
            c => c.RequestCompression = AxiosCompression.Gzip));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("HttpContent", ex.Message);
    }

    [Fact]
    public async Task Multipart_IsRejected()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/upload",
            new { title = "a" },
            c =>
            {
                c.ContentType = AxiosContentType.MultipartFormData;
                c.RequestCompression = AxiosCompression.Gzip;
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("multipart", ex.Message);
    }

    [Fact]
    public async Task ManualContentEncodingHeader_IsRejected()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/x",
            new Person("alice", 42),
            c =>
            {
                c.Headers.Set("Content-Encoding", "gzip");
                c.RequestCompression = AxiosCompression.Gzip;
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("Content-Encoding", ex.Message);
    }

    [Fact]
    public async Task UndefinedAlgorithm_IsRejected()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/x",
            new Person("alice", 42),
            c => c.RequestCompression = (AxiosCompression)99));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("未定义", ex.Message);
    }

    [Fact]
    public async Task Compression_SurvivesRetry_EachAttemptIsCompressed()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(req => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = { Policy = new RetryPolicy(1, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });

        var response = await client.PostAsync("x", new Person("alice", 42), c => c.RequestCompression = AxiosCompression.Gzip);

        Assert.Equal(200, response.Status);

        // 重试复制的是压缩后的模板：两次尝试的正文都是 gzip，且都能解回同一份 JSON。
        Assert.Equal(2, server.Received.Count);
        foreach (var request in server.Received)
        {
            Assert.Equal("gzip", request.Headers["content-encoding"]);
            Assert.Equal("""{"name":"alice","age":42}""", Encoding.UTF8.GetString(Decompress(request.Body, AxiosCompression.Gzip)));
        }
    }

    [Fact]
    public async Task ReplayRequest_CarriesRequestCompression()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var first = await source.PostAsync("x", new Person("alice", 42), c => c.RequestCompression = AxiosCompression.Gzip);
        await target.RequestAsync(first.Config!);

        // 回放把 RequestCompression 一起带走：目标实例按自己的配置重新压缩，不是原样重放字节。
        Assert.Equal("gzip", server.Received[1].Headers["content-encoding"]);
        Assert.Equal("""{"name":"alice","age":42}""", Encoding.UTF8.GetString(Decompress(server.Received[1].Body, AxiosCompression.Gzip)));
    }

    private static byte[] Decompress(byte[] body, AxiosCompression compression)
    {
        using var input = new MemoryStream(body);
        using Stream decompressor = compression switch
        {
            AxiosCompression.Gzip => new GZipStream(input, CompressionMode.Decompress),
            AxiosCompression.Deflate => new DeflateStream(input, CompressionMode.Decompress),
            AxiosCompression.Brotli => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };

        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }
}
