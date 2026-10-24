using System.Collections;
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

        // 延迟执行的迭代器在物化该集合时就抛错：归成「请求体构建失败」（ERR_BAD_REQUEST），
        // 原始异常留在 InnerException 上，已经产出的流在回滚里被就地释放
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new ThrowingCollectionRequest { Files = YieldThenThrow(stream) }));

        Assert.Equal("ERR_BAD_REQUEST", ex.Code);
        Assert.Contains("ThrowingCollectionRequest", ex.Message);   // 绑定期失败带模型名（装配期只剩表单字典）
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(ex.InnerException).Message);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Multipart_DeclaredCollection_IsMaterializedWhenConfigIsBuilt()
    {
        var enumerations = 0;
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 声明了覆盖项的集合在绑定期就枚举并物化：延迟枚举会让「同一份配置重放」二次枚举调用方的序列，
        // 只能枚举一次的迭代器第二次就是空的（GetUri 只建配置、不发请求，正好观察枚举时机）
        client.GetUri(new DeclaredCollectionRequest { Chunks = CountEachChunk(() => enumerations++) });

        Assert.Equal(1, enumerations);
    }

    [Fact]
    public async Task Multipart_ConfigureSeesRawPartValues()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        object? seen = null;

        await client.RequestAsync(
            new UploadRequest
            {
                Title = "t",
                Blob = new byte[] { 1 },
                Extra = new byte[] { 2 },
                Tags = new Dictionary<string, string> { ["k"] = "v" }
            },
            c => seen = c.Data);

        // 公开的 config.Data 里不能出现内部包装：有声明的部件也只登记覆盖，值保持原样
        var data = Assert.IsAssignableFrom<IDictionary<string, object?>>(seen);
        Assert.Equal("t", data["Title"]);
        Assert.IsType<byte[]>(data["Blob"]);
        Assert.IsType<byte[]>(data["Extra"]);
        Assert.IsType<Dictionary<string, string>>(data["meta"]);
    }

    [Fact]
    public async Task Multipart_DeclaredMemoryShapes_AreFilePartsWithDeclaredMetadata()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var memory = Encoding.ASCII.GetBytes("mem-data");
        var readOnly = Encoding.ASCII.GetBytes("rom-data");
        var segment = Encoding.ASCII.GetBytes("seg-data");
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("stream-data"));

        await client.RequestAsync(new DeclaredMemoryShapesRequest
        {
            Memory = new Memory<byte>(memory),
            ReadOnly = new ReadOnlyMemory<byte>(readOnly),
            Segment = new ArraySegment<byte>(segment),
            Stream = stream
        });

        var received = Assert.Single(server.Received);
        var body = Body(received);

        // 声明覆盖对四种「内存 / 流」形态一视同仁：文件名与 Content-Type 都来自声明
        Assert.Contains("name=Memory; filename=mem.bin", body);
        Assert.Contains("application/x-memory", body);
        Assert.Contains("name=ReadOnly; filename=rom.bin", body);
        Assert.Contains("application/x-readonly", body);
        Assert.Contains("name=Segment; filename=seg.bin", body);
        Assert.Contains("application/x-segment", body);
        Assert.Contains("name=Stream; filename=stream.bin", body);
        Assert.Contains("application/x-stream", body);

        Assert.True(received.Body.AsSpan().IndexOf(memory) >= 0);
        Assert.True(received.Body.AsSpan().IndexOf(readOnly) >= 0);
        Assert.True(received.Body.AsSpan().IndexOf(segment) >= 0);
        Assert.Contains("stream-data", body);
        Assert.False(stream.CanRead);   // 成功路径同样随请求释放调用方的流
    }

    [Fact]
    public async Task Multipart_PlainMemoryShapes_BecomeFileParts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new PlainMemoryShapesRequest
        {
            Memory = new Memory<byte>(Encoding.ASCII.GetBytes("m")),
            ReadOnly = new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes("r")),
            Segment = new ArraySegment<byte>(Encoding.ASCII.GetBytes("s"))
        });

        // 未声明时保持既有默认：字段名即文件名、octet-stream
        var body = Body(Assert.Single(server.Received));
        Assert.Contains("name=Memory; filename=Memory", body);
        Assert.Contains("name=ReadOnly; filename=ReadOnly", body);
        Assert.Contains("name=Segment; filename=Segment", body);
    }

    [Fact]
    public async Task Multipart_IllegalDeclaredContentTypeOnStream_ReleasesStream()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("payload"));
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 声明值非法在「流已交出所有权」之后才被发现：必须就地释放，且归成选项错误
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new IllegalDeclaredContentTypeOnStreamRequest { File = stream }));

        Assert.Equal("ERR_BAD_OPTION_VALUE", ex.Code);
        Assert.Contains("Content-Type", ex.Message);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Multipart_AssemblyFailure_ReportsModelName()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 装配期（config.Data 已只剩表单）失败也要能指出是哪个请求模型，而不是内部容器类型名
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new LazyThrowingPartRequest()));

        Assert.Equal("ERR_BAD_REQUEST", ex.Code);
        Assert.Contains(nameof(LazyThrowingPartRequest), ex.Message);
        Assert.DoesNotContain("Dictionary", ex.Message);
    }

    [Fact]
    public async Task Multipart_IllegalDeclaredFileName_ReportsOptionError()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 声明的值写错是配置错误：与 [Part].Name 非法同一分类，不能报成「请求体序列化失败」
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new IllegalDeclaredFileNameRequest { Blob = [1, 2, 3] }));

        Assert.Equal("ERR_BAD_OPTION_VALUE", ex.Code);
        Assert.Contains("文件名", ex.Message);
        // 中间那层是库内部的选项错误，原始原因（FormFile 构造期的 ArgumentException）留在最里层
        Assert.IsType<ArgumentException>(ex.InnerException?.InnerException);
    }

    [Fact]
    public async Task Multipart_IllegalDeclaredContentType_ReportsOptionError()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new IllegalDeclaredContentTypeRequest { Blob = [1, 2, 3] }));

        // Content-Type 是逐个部件写进头部的取值，控制字符会被 FormFile 在构造期拦下
        Assert.Equal("ERR_BAD_OPTION_VALUE", ex.Code);
        Assert.Contains("Content-Type", ex.Message);
    }

    [Fact]
    public async Task Multipart_IllegalDeclaredNameOnFormFile_ReportsOptionErrorAndReleasesStream()
    {
        var stream = new MemoryStream("payload"u8.ToArray());
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 显式声明的字段名压过 FormFile 自带的 FieldName：非法同样是配置错误，且已交进来的流必须释放
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new IllegalDeclaredNameOnFileRequest { File = new FormFile("a.bin", stream) }));

        Assert.Equal("ERR_BAD_OPTION_VALUE", ex.Code);
        Assert.False(stream.CanRead);
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
    public async Task Multipart_RawStreamPassthrough_IsDisposedWhenLaterPartThrows()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        // 属性取值经反射，异常会被包进 TargetInvocationException（这里只关心「装配中断 + 流被释放」）
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(new RawStreamThenThrowRequest { File = stream }));

        Assert.Contains("boom", ex.ToString());

        // 没声明 FileName/ContentType 的裸 Stream 原样透传（不包装成 FormFile）：
        // 装配在后续属性上中断时它还没被 builder 包成 StreamContent，容器 Dispose 覆盖不到，
        // 只能靠回滚就地释放 —— 否则调用方的 FileStream 句柄永久泄漏
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Multipart_DictionaryOfFormFiles_ReleasesStreamsWhenLaterPartThrows()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(new DictionaryFormFileThenThrowRequest
            {
                Files = new Dictionary<string, FormFile>
                {
                    ["a"] = new FormFile("a.txt", stream, "text/plain")
                }
            }));

        Assert.Contains("boom", ex.ToString());

        // 字典整体透传给 builder。它的元素是 KeyValuePair 值类型，
        // 协变到 IEnumerable<object?> 不成立：回滚必须递归进字典，否则装配失败时这里的流永久泄漏
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Multipart_NonGenericCollection_ReleasesStreamsWhenLaterPartThrows()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestAsync(new ArrayListThenThrowRequest
            {
                Files = new ArrayList { new FormFile("a.txt", stream, "text/plain") }
            }));

        Assert.Contains("boom", ex.ToString());

        // ArrayList 只实现非泛型 IEnumerable：同样不能被回滚跳过
        Assert.False(stream.CanRead);
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
    private sealed class IllegalDeclaredFileNameRequest : IApiRequest<object>
    {
        /// <summary>文件名落在 quoted-string 里：引号会破坏头部语法，属配置错误。</summary>
        [Part(FileName = "a\"b.txt")]
        public byte[]? Blob { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class IllegalDeclaredContentTypeRequest : IApiRequest<object>
    {
        [Part(ContentType = "text/plain\r\nX-Injected: 1")]
        public byte[]? Blob { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class IllegalDeclaredNameOnFileRequest : IApiRequest<object>
    {
        /// <summary>显式声明的字段名压过值自带的 FieldName，非法时连流一起回收。</summary>
        [Part("bad\"name")]
        public FormFile? File { get; set; }
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
    private sealed class RawStreamThenThrowRequest : IApiRequest<object>
    {
        /// <summary>未声明 FileName / ContentType → 原样透传，不包装成 <see cref="FormFile"/>。</summary>
        [Part]
        public Stream? File { get; set; }

        /// <summary>取值即抛：装配在后续属性上中断，此时前面的裸流还没交给 builder。</summary>
        [Part]
        public string? Boom => throw new InvalidOperationException("boom");
    }

    [Post("upload")]
    [Multipart]
    private sealed class DictionaryFormFileThenThrowRequest : IApiRequest<object>
    {
        /// <summary>未声明 FileName / ContentType → 字典整体透传给 builder，值里的 FormFile 仍归库释放。</summary>
        [Part]
        public Dictionary<string, FormFile> Files { get; set; } = new();

        /// <summary>取值即抛：装配在后续属性上中断，此时字典还没交给 builder。</summary>
        [Part]
        public string? Boom => throw new InvalidOperationException("boom");
    }

    [Post("upload")]
    [Multipart]
    private sealed class ArrayListThenThrowRequest : IApiRequest<object>
    {
        /// <summary>非泛型集合：只实现 IEnumerable，回滚同样要逐项回收。</summary>
        [Part]
        public ArrayList Files { get; set; } = new();

        [Part]
        public string? Boom => throw new InvalidOperationException("boom");
    }

    [Post("upload")]
    [Multipart]
    private sealed class DeclaredCollectionRequest : IApiRequest<object>
    {
        [Part(ContentType = "application/pdf")]
        public IEnumerable<byte[]>? Chunks { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class DeclaredMemoryShapesRequest : IApiRequest<object>
    {
        [Part(FileName = "mem.bin", ContentType = "application/x-memory")]
        public Memory<byte> Memory { get; set; }

        [Part(FileName = "rom.bin", ContentType = "application/x-readonly")]
        public ReadOnlyMemory<byte> ReadOnly { get; set; }

        [Part(FileName = "seg.bin", ContentType = "application/x-segment")]
        public ArraySegment<byte> Segment { get; set; }

        [Part(FileName = "stream.bin", ContentType = "application/x-stream")]
        public Stream? Stream { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class PlainMemoryShapesRequest : IApiRequest<object>
    {
        public Memory<byte> Memory { get; set; }

        public ReadOnlyMemory<byte> ReadOnly { get; set; }

        public ArraySegment<byte> Segment { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class IllegalDeclaredContentTypeOnStreamRequest : IApiRequest<object>
    {
        [Part(ContentType = "text/plain\r\nX-Injected: 1")]
        public Stream? File { get; set; }
    }

    [Post("upload")]
    [Multipart]
    private sealed class LazyThrowingPartRequest : IApiRequest<object>
    {
        /// <summary>装配到这一项才枚举并抛错：失败发生在 builder 里，config.Data 已只剩表单。</summary>
        public IEnumerable<int> Values => ThrowingValues();

        private static IEnumerable<int> ThrowingValues()
        {
            yield return 1;
            throw new InvalidOperationException("boom");
        }
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

    /// <summary>每次开始迭代就记一次数（延迟枚举在 GetUri 阶段被触发时能数到 0）。</summary>
    private static IEnumerable<byte[]> CountEachChunk(Action onEnumerate)
    {
        onEnumerate();
        yield return [1, 2, 3];
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
