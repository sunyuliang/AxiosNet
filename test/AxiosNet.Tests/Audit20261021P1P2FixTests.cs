using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-21 审计 P1/P2 项（见 <c>.workbuddy/reports/2026-10-21-bug-audit.md</c>）的回归护栏：
/// <list type="bullet">
/// <item>P1-4：<c>FormFile.FromPath</c> 文件名校验失败时不得泄漏已打开的 FileStream；</item>
/// <item>P1-5：<c>transformResponse</c> 抛出的异常必须归一为 <c>ERR_BAD_RESPONSE</c>（与 transformRequest 对称）；</item>
/// <item>P1-6：XML 声明里的 <c>encoding</c> 必须跟随 <c>RequestEncoding</c>，与实际字节一致；</item>
/// <item>P2-7：<c>host:port/path</c> 形态的相对 URL 不能被当成绝对地址绕过 baseURL；</item>
/// <item>P2-8：两参 <c>FromPath</c> 的第二个参数保持原语义（Content-Type），不改调用契约；</item>
/// <item>P2-9：<c>Expires</c> / <c>Last-Modified</c> / <c>Allow</c> 不再是内容头，无正文的 GET 不得凭空造出正文。</item>
/// </list>
/// </summary>
public sealed class Audit20261021P1P2FixTests
{
    // ------------------------------------------------------------------
    // P1-4：FromPath 校验失败不泄漏文件句柄
    // ------------------------------------------------------------------

    [Fact]
    public void FormFile_FromPath_InvalidFileName_DoesNotLeakFileHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 双引号会破坏 Content-Disposition 的 quoted-string，构造函数必须拒绝。
            Assert.Throws<ArgumentException>(() => FormFile.FromPath(path, "bad\"name.txt", "text/plain"));

            // 句柄已释放：能以独占方式打开（Windows/Unix 都由 FileShare 语义约束）。
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            // 也能删除（Windows 上句柄未关时这一步会抛 IOException）。
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

    // ------------------------------------------------------------------
    // P1-5：transformResponse 的异常归一
    // ------------------------------------------------------------------

    [Fact]
    public async Task TransformResponse_Throws_IsWrappedAsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"a\":1}"),
            ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<object>(
            server.Url + "item",
            c => c.TransformResponse.Add((_, _) => throw new InvalidOperationException("boom"))));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.IsType<InvalidOperationException>(error.Cause);
    }

    [Fact]
    public async Task TransformResponse_SuccessPath_StillTransforms()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"a\":1}"),
            ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(
            server.Url + "item",
            c => c.TransformResponse.Add((_, _) => "unwrapped"));

        Assert.Equal("unwrapped", response.Data);
    }

    // ------------------------------------------------------------------
    // P1-6：XML 声明的 encoding 跟随 RequestEncoding
    // ------------------------------------------------------------------

    [Fact]
    public async Task XmlSerializer_Declaration_MatchesRequestEncoding()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            ContentSerializer = XmlContentSerializer.Instance
        });

        await client.PostAsync(
            server.Url + "xml",
            new XmlProbeDto { Name = "中文" },
            c => c.RequestEncoding = Encoding.Unicode);

        var request = server.Received[0];

        // 字节确实是 UTF-16LE（'<' 0x3C 后跟 0x00），而不是声明 utf-8 却发 UTF-16。
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
        var text = XmlContentSerializer.Instance.Serialize(
            new XmlProbeDto { Name = "abc" },
            typeof(XmlProbeDto),
            new AxiosConfig());

        Assert.Contains("encoding=\"utf-8\"", text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // P2-7：host:port 形态的相对 URL 与 baseURL 组合
    // ------------------------------------------------------------------

    [Fact]
    public void GetUri_HostLikeRelativeUrl_IsCombinedWithBaseAddress()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/api" });

        // axios 的 isAbsoluteURL 要求 scheme://，"localhost:8080/items" 属相对路径。
        Assert.Equal(
            "http://example.com/api/localhost:8080/items",
            client.GetUri(new AxiosConfig { Url = "localhost:8080/items" }));
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

    // ------------------------------------------------------------------
    // P2-8：两参 FromPath 的第二参数保持「Content-Type」原语义
    // ------------------------------------------------------------------

    // 审计曾提议让两参 FromPath 校验第二个参数必须是媒体类型，以拦下
    // FormFile.FromPath(path, "report.txt") 这类误用 —— 已按使用者意见撤销：
    // 调用契约不变，误用由使用方自己注意（三参重载是唯一能改文件名的入口）。
    // 这里只锁定「不改契约」这件事本身。
    [Fact]
    public void FormFile_FromPath_TwoArguments_KeepsContentTypeSemantics()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 兼容原有写法：两参的第二个参数仍是 Content-Type，文件名取路径原名。
            var file = FormFile.FromPath(path, "application/pdf");

            Assert.Equal(Path.GetFileName(path), file.FileName);
            Assert.Equal("application/pdf", file.ContentType);

            file.Content.Dispose();

            // 误用不会被拦：第二个字符串原样成为 Content-Type。这是刻意保留的行为 ——
            // 库不做「猜你想传文件名」的判断，要不踩就得用三参重载。
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

    // ------------------------------------------------------------------
    // P2-9：Allow / Expires / Last-Modified 属于 BCL 的「内容头」
    // ------------------------------------------------------------------

    // 审计时把这三个头移出内容头集合的「修法」被这两条用例否掉了：
    // HttpRequestHeaders 对它们返回 false（BCL 把它们登记在 HttpContentHeaders 下），
    // 移出后头会被静默丢弃 —— 比补一个空正文（Content-Length: 0）更糟。
    // 这里锁定行为：头必须送达；为此附带一个空正文是 BCL 头模型的代价。
    [Theory]
    [InlineData("Expires", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Allow", "GET, POST")]
    public async Task Get_WithContentModeledResponseHeader_DeliversHeader(string name, string value)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set(name, value));

        var request = server.Received[0];
        Assert.Equal(value, request.Headers[name]);
    }

    [Fact]
    public async Task Get_WithPlainCustomHeader_SendsNoBody()
    {
        // 对照：普通自定义头走请求头，不会为它造出正文。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            ContentType: "application/json; charset=utf-8"));

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
        // 对照：真正的 entity header 仍按原语义落到正文上（这里只验证不会被静默丢掉）。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            ContentType: "application/json; charset=utf-8"));

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
