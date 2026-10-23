using System.Text;
using System.Text.Json.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// D5-B：声明式 multipart——<c>[Multipart]</c>（模型自身即表单）+ <c>[Part]</c>（字段名 / 文件名 / 类型覆盖），
/// 装配完全复用核心包的 <c>MultipartBodyBuilder</c>（boundary、名字校验、所有权规则不变）。
/// </summary>
public sealed class ApiClientMultipartTests
{
    [Fact]
    public async Task Multipart_Model_IsAssembledAsForm()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new UploadRequest
        {
            Title = "年报",
            Blob = [1, 2, 3],
            Extra = "文本内容"u8.ToArray(),
            Tags = new Dictionary<string, string> { ["env"] = "test" }
        });

        var received = Assert.Single(server.Received);
        Assert.StartsWith("multipart/form-data; boundary=", ContentType(received));

        var body = Body(received);
        Assert.Contains("name=Title", body);                       // 字符串 → 普通字段
        Assert.Contains("年报", body);
        Assert.Contains("name=Blob; filename=Blob", body);          // byte[] → 文件部件（默认 filename = 字段名）
        Assert.Contains("name=Extra; filename=a.txt", body);        // [Part(FileName)] 覆盖文件名
        Assert.Contains("text/plain", body);                        // [Part(ContentType)] 覆盖类型
        Assert.Contains("文本内容", body);
        Assert.Contains("meta[env]", body);                        // 字典 → builder 展平 a[b]（BCL 可能给名字加引号）
        Assert.True(received.Body.AsSpan().IndexOf(new byte[] { 1, 2, 3 }) >= 0); // 字节原样到达
    }

    [Fact]
    public async Task Multipart_UnannotatedProperties_ParticipateAsParts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new PlainUploadRequest { Note = "n1", Payload = [9] });

        var body = Body(Assert.Single(server.Received));
        Assert.Contains("name=Note", body);
        Assert.Contains("name=Payload; filename=Payload", body);
    }

    [Fact]
    public async Task Multipart_FormFile_AttributeOverridesValueDeclaration()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new DocumentUploadRequest
        {
            Doc = new FormFile("doc.pdf", "pdf-bytes"u8.ToArray()),
            Signed = new FormFile("sig.dat", "sig"u8.ToArray(), "application/zip")
        });

        var received = Assert.Single(server.Received);
        var body = Body(received);

        // [Part(ContentType)] 覆盖 FormFile 未声明的类型；[Part("upload")] 覆盖字段名
        Assert.Contains("name=Doc; filename=doc.pdf", body);
        Assert.Contains("application/pdf", body);
        Assert.Contains("name=upload; filename=sig.dat", body);
        Assert.Contains("application/zip", body);
    }

    [Fact]
    public async Task Multipart_Collection_RepeatsParts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new MultiFileRequest
        {
            Files =
            {
                new FormFile("a.txt", "a"u8.ToArray(), "text/plain"),
                new FormFile("b.txt", "b"u8.ToArray(), "text/plain")
            }
        });

        var body = Body(Assert.Single(server.Received));
        Assert.Equal(2, CountOccurrences(body, "filename=a.txt") + CountOccurrences(body, "filename=b.txt"));
        Assert.Contains("name=Files", body);
    }

    [Fact]
    public async Task Multipart_FormFileFieldName_WinsOverDefaultName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SingleFileRequest
        {
            File = new FormFile("report.txt", "x"u8.ToArray(), "text/plain") { FieldName = "custom" }
        });

        // 没写 [Part(Name)] 时，值自带的 FieldName 优先于属性名
        Assert.Contains("name=custom; filename=report.txt", Body(Assert.Single(server.Received)));
    }

    [Fact]
    public async Task Multipart_JsonPropertyName_IsDefaultPartName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new JsonNamedPartRequest { DisplayName = "n" });

        // 默认部件名与嵌套对象展平一致：[JsonPropertyName] 优先于属性名
        Assert.Contains("name=display_name", Body(Assert.Single(server.Received)));
    }

    [Fact]
    public async Task Multipart_FileDeclarationOnDictionary_StillFlattens()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FileDeclaredMapRequest
        {
            Meta = new Dictionary<string, string> { ["env"] = "test" }
        });

        // 字典不是文件：即便声明了文件名也应整体透传，按 meta[env] 展平
        var body = Body(Assert.Single(server.Received));
        Assert.Contains("meta[env]", body);
        Assert.DoesNotContain("name=Key", body);
    }

    [Fact]
    public async Task Multipart_FailingPart_ReleasesEarlierStreams()
    {
        var stream = new MemoryStream("payload"u8.ToArray());
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync(new ThrowingCollectionRequest { Files = YieldThenThrow(stream) }));

        Assert.Contains("boom", ex.Message);
        Assert.False(stream.CanRead);   // 已包进 FormFile 的流在失败路径上被就地释放
    }

    [Fact]
    public async Task Multipart_StringWithFileName_StaysField()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new TextFieldRequest { Text = "hello" });

        var body = Body(Assert.Single(server.Received));
        Assert.Contains("name=Text", body);
        Assert.DoesNotContain("filename", body);   // 字符串永远是普通字段，不发文件部件
    }

    [Fact]
    public async Task Multipart_AllNullProperties_SendsNoBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new UploadRequest());

        var received = Assert.Single(server.Received);
        Assert.DoesNotContain("name=", Body(received));
    }

    [Fact]
    public async Task Multipart_ExplicitContentTypeWithBoundary_IsPreserved()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(
            new UploadRequest { Title = "t" },
            configure => configure.ContentType = "multipart/form-data; boundary=----axios-net");

        var received = Assert.Single(server.Received);
        Assert.Contains("----axios-net", ContentType(received));   // BCL 会给带保留字符的 boundary 加引号
        Assert.Contains("----axios-net", Body(received));
    }

    [Fact]
    public void Multipart_WithBody_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new ConflictingBodyRequest()));

        Assert.Contains("[Multipart]", ex.Message);
        Assert.Contains("[Body]", ex.Message);
    }

    [Fact]
    public void Part_WithoutMultipart_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new PartWithoutMultipartRequest()));

        Assert.Contains("[Part]", ex.Message);
        Assert.Contains("[Multipart]", ex.Message);
    }

    [Fact]
    public void DuplicatePartName_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new DuplicatePartRequest()));

        Assert.Contains("Part", ex.Message);
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void BlankPartName_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new BlankPartNameRequest()));

        Assert.Contains("不能为空白", ex.Message);
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Post("upload")]
    [Multipart]
    private sealed class UploadRequest : IApiRequest<object>
    {
        [Part]
        public string? Title { get; set; }

        [Part]
        public byte[]? Blob { get; set; }

        [Part(FileName = "a.txt", ContentType = "text/plain")]
        public byte[]? Extra { get; set; }

        [Part("meta")]
        public Dictionary<string, string>? Tags { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class PlainUploadRequest : IApiRequest<object>
    {
        public string? Note { get; set; }

        public byte[]? Payload { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class DocumentUploadRequest : IApiRequest<object>
    {
        [Part(ContentType = "application/pdf")]
        public FormFile? Doc { get; set; }

        [Part("upload")]
        public FormFile? Signed { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class MultiFileRequest : IApiRequest<object>
    {
        [Part]
        public List<FormFile> Files { get; set; } = new();
    }

    [Post("upload")]
    [Multipart]
    private sealed class TextFieldRequest : IApiRequest<object>
    {
        [Part(FileName = "x.txt")]
        public string? Text { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class SingleFileRequest : IApiRequest<object>
    {
        [Part]
        public FormFile? File { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class JsonNamedPartRequest : IApiRequest<object>
    {
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class FileDeclaredMapRequest : IApiRequest<object>
    {
        [Part("meta", FileName = "ignored.bin")]
        public Dictionary<string, string>? Meta { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class ThrowingCollectionRequest : IApiRequest<object>
    {
        [Part(ContentType = "text/plain")]
        public IEnumerable<Stream> Files { get; set; } = [];
    }

    [Post("upload")]
    [Multipart]
    private sealed class ConflictingBodyRequest : IApiRequest<object>
    {
        [Body]
        public Person? Payload { get; set; }
    }

    [Post("upload")]
    private sealed class PartWithoutMultipartRequest : IApiRequest<object>
    {
        [Part]
        public string? Title { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class DuplicatePartRequest : IApiRequest<object>
    {
        [Part("dup")]
        public string? A { get; set; }

        [Part("dup")]
        public string? B { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class BlankPartNameRequest : IApiRequest<object>
    {
        [Part("  ")]
        public string? A { get; set; }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.TryGetValue("Content-Type", out var value) ? value : string.Empty;

    private static IEnumerable<Stream> YieldThenThrow(Stream stream)
    {
        yield return stream;
        throw new InvalidOperationException("boom");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
