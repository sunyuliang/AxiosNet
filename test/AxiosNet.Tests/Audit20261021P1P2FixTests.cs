using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary><c>FormFile.FromPath</c> 不泄漏文件句柄、<c>transformResponse</c> 异常归一为 <c>ERR_BAD_RESPONSE</c>、XML 声明跟随 <c>RequestEncoding</c>、host:port 形态的相对 URL 与 baseURL 组合、内容头送达。</summary>
public sealed class Audit20261021P1P2FixTests
{
    [Fact]
    public void FormFile_FromPath_InvalidFileName_DoesNotLeakFileHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 双引号会破坏 Content-Disposition 的 quoted-string。
            Assert.Throws<ArgumentException>(() => FormFile.FromPath(path, "bad\"name.txt", "text/plain"));

            // 句柄已释放：能以独占方式打开。
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            // 也能删除。
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TransformResponse_Throws_IsWrappedAsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"a\":1}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<object>(server.Url + "item", c => c.TransformResponse.Add((_, _) => throw new InvalidOperationException("boom"))));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.IsType<InvalidOperationException>(error.Cause);
    }

    [Fact]
    public async Task TransformResponse_SuccessPath_StillTransforms()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"a\":1}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "item", c => c.TransformResponse.Add((_, _) => "unwrapped"));

        Assert.Equal("unwrapped", response.Data);
    }

    [Fact]
    public async Task XmlSerializer_Declaration_MatchesRequestEncoding()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        await client.PostAsync(server.Url + "xml", new XmlProbeDto { Name = "中文" }, c => c.Serialization.RequestEncoding = Encoding.Unicode);

        var request = server.Received[0];

        // 字节确实是 UTF-16LE（'<' 0x3C 后跟 0x00）。
        Assert.True(request.Body.Length > 1);
        Assert.Equal(0x3C, request.Body[0]);
        Assert.Equal(0x00, request.Body[1]);

        var declaration = Encoding.Unicode.GetString(request.Body, 0, Math.Min(request.Body.Length, 96));
        Assert.Contains("encoding=\"utf-16\"", declaration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("charset=utf-16", request.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XmlSerializer_WithoutRequestEncoding_DeclaresUtf8()
    {
        var text = XmlContentSerializer.Instance.Serialize(new XmlProbeDto { Name = "abc" }, typeof(XmlProbeDto), new AxiosConfig());

        Assert.Contains("encoding=\"utf-8\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetUri_HostLikeRelativeUrl_IsCombinedWithBaseAddress()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/api" });

        // isAbsoluteURL 要求 scheme://，host:port 形态属相对路径。
        Assert.Equal("http://example.com/api/localhost:8080/items", client.GetUri(new AxiosConfig { Url = "localhost:8080/items" }));
    }

    [Fact]
    public void GetUri_AbsoluteUrl_StillIgnoresBaseAddress()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/api" });

        Assert.Equal("https://other.test/x?q=1", client.GetUri(new AxiosConfig { Url = "https://other.test/x?q=1" }));
    }

    [Fact]
    public void GetUri_FileScheme_IsStillRejected()
    {
        using var client = new AxiosClient();

        var error = Assert.Throws<AxiosException>(() => client.GetUri(new AxiosConfig { Url = "file:///etc/passwd" }));
        Assert.Equal(AxiosException.ErrInvalidUrl, error.Code);
    }

    [Fact]
    public void FormFile_FromPath_TwoArguments_KeepsContentTypeSemantics()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 两参的第二个参数是 Content-Type，文件名取路径原名。
            var file = FormFile.FromPath(path, "application/pdf");

            Assert.Equal(Path.GetFileName(path), file.FileName);
            Assert.Equal("application/pdf", file.ContentType);

            file.Content.Dispose();

            // 误用不会被拦：第二个字符串原样成为 Content-Type。
            var misused = FormFile.FromPath(path, "report.txt");
            Assert.Equal("report.txt", misused.ContentType);
            Assert.Equal(Path.GetFileName(path), misused.FileName);
            misused.Content.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FormFile_FromPath_ThreeArguments_SetsFileName()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            var file = FormFile.FromPath(path, "report.txt", "text/plain");

            Assert.Equal("report.txt", file.FileName);
            Assert.Equal("text/plain", file.ContentType);

            file.Content.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 这三个头由 BCL 归为内容头，必须送达（代价是附带一个空正文）。
    [Theory]
    [InlineData("Expires", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Allow", "GET, POST")]
    public async Task Get_WithContentModeledResponseHeader_DeliversHeader(string name, string value)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set(name, value));

        var request = server.Received[0];
        Assert.Equal(value, request.Headers[name]);
    }

    [Fact]
    public async Task Get_WithPlainCustomHeader_SendsNoBody()
    {
        // 普通自定义头走请求头，不会为它造出正文。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("X-Trace", "1"));

        var request = server.Received[0];
        Assert.Equal("1", request.Headers["X-Trace"]);
        Assert.Empty(request.Body);
        Assert.False(request.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task Get_WithContentHeader_StillAllowedToCreateBody()
    {
        // 真正的 entity header 仍按原语义落到正文上。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("Content-Language", "zh-CN"));

        var request = server.Received[0];
        Assert.Equal("zh-CN", request.Headers["Content-Language"]);
    }
}

/// <summary>XML 序列化用的公开 DTO（XmlSerializer 要求公开无参构造 + 公开可读写属性）。</summary>
public sealed class XmlProbeDto
{
    public string Name { get; set; } = string.Empty;
}
