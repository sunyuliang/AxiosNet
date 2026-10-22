using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 全局排查修复：multipart 异常路径的资源所有权，以及 XML 声明的编码与正文/头的一致性。
/// <para>
/// 两条都是「测试全绿但行为错误」的缺陷：前者只在组装失败时才暴露（调用方的 FileStream 永久不释放），
/// 后者只在 ContentType 声明了非 UTF-8 charset 时暴露（正文与声明互相矛盾且无报错）。
/// </para>
/// </summary>
public sealed class Audit20261022MultipartXmlFixTests
{
    // ------------------------------------------------------------------
    // multipart：字段名校验失败时，调用方的流必须被释放
    // ------------------------------------------------------------------

    [Fact]
    public void FormFile_InvalidFieldName_ThrowsAtConstruction()
    {
        // FieldName 与 FileName 一样落在 quoted-string 里：赋值处就拦，不把它拖到组装期
        var ex = Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = "bad\"name" });

        Assert.Contains("字段名", ex.Message);
    }

    [Theory]
    [InlineData("bad\"name")]
    [InlineData("bad\\name")]
    [InlineData("bad\r\nname")]
    [InlineData("bad\0name")]
    public void FormFile_InvalidFieldName_RejectsEveryDispositionSeparator(string fieldName)
    {
        Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = fieldName });
    }

    [Fact]
    public async Task Multipart_InvalidNestedFieldName_ReleasesCallerStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var payload = new Dictionary<string, object?> { ["bad\"name"] = stream };

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", payload, c => c.ContentType = AxiosContentType.MultipartFormData));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);

        // 关键断言：部件没能加入容器，容器的 Dispose 覆盖不到它 —— 必须由组装方就地释放
        Assert.False(stream.CanRead);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_RootFormFileWithoutFieldName_ReportsBadOptionAndReleasesStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var file = new FormFile("a.txt", stream);

        // 根节点没有字段名：这是调用方的模型装配错误（ERR_BAD_OPTION_VALUE），
        // 不是「请求体序列化失败」（ERR_BAD_REQUEST）
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", file, c => c.ContentType = AxiosContentType.MultipartFormData));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.False(stream.CanRead);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_SuccessPath_StillReleasesCallerStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var file = new FormFile("a.txt", stream);

        await client.PostAsync(
            "upload",
            new { file },
            c => c.ContentType = AxiosContentType.MultipartFormData);

        Assert.False(stream.CanRead);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Multipart_InvalidFieldNameOnFormFile_DoesNotReachAssembler()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 非法 FieldName 在构造期就被拒：既不会发出请求，也不会走到「已取得流所有权」的组装路径
        Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = "a;b\"c" });

        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // XML：声明里的 encoding 必须与头、正文三者一致
    // ------------------------------------------------------------------

    [Fact]
    public async Task Xml_ContentTypeCharset_DrivesDeclarationAndBody()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "hello" },
            c =>
            {
                c.ContentSerializer = XmlContentSerializer.Instance;
                c.ContentType = AxiosContentType.Xml + "; charset=iso-8859-1";
            });

        var request = server.Received[0];
        var document = Encoding.Latin1.GetString(request.Body);

        // 头、XML 声明、正文编码三者统一由 ContentType 的 charset 决定
        Assert.Contains("charset=iso-8859-1", request.Headers["Content-Type"]);
        Assert.Contains("encoding=\"iso-8859-1\"", document);
        Assert.Contains("<Name>hello</Name>", document);
    }

    [Fact]
    public async Task Xml_RequestEncoding_DrivesDeclarationWhenNoCharset()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "hello" },
            c =>
            {
                c.ContentSerializer = XmlContentSerializer.Instance;
                c.ContentType = AxiosContentType.Xml;
                c.RequestEncoding = Encoding.Latin1;
            });

        var document = Encoding.Latin1.GetString(server.Received[0].Body);
        Assert.Contains("encoding=\"iso-8859-1\"", document);
        Assert.Contains("charset=iso-8859-1", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task Xml_DefaultEncoding_RemainsUtf8()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "中文" },
            c => c.ContentSerializer = XmlContentSerializer.Instance);

        var request = server.Received[0];
        var document = Encoding.UTF8.GetString(request.Body);

        // 序列化器自报 media type：头是 application/xml，未显式设 RequestEncoding 时不追加 charset
        Assert.Equal(AxiosContentType.Xml, request.Headers["Content-Type"]);
        Assert.Contains("encoding=\"utf-8\"", document);
        Assert.Contains("中文", document);
    }

    public sealed class XmlPayload
    {
        public string? Name { get; set; }
    }
}
