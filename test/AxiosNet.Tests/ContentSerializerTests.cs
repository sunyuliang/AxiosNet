using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>XML 序列化用的公开类型（XmlSerializer 要求 public + 无参构造）。</summary>
public sealed class XmlOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>显式声明根命名空间的类型：内置 XML 序列化器不能把命名空间吞掉。</summary>
[XmlRoot("Order", Namespace = "urn:ax")]
public sealed class NamespacedOrder
{
    public int Id { get; set; }
}

/// <summary>二进制序列化用的公开类型（字段语义见二进制用例中的 payload 布局）。</summary>
public sealed class BinaryOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Tail { get; set; }
}

/// <summary>
/// 格式无关序列化抽象（<see cref="IContentSerializer"/>）：
/// 文本格式两个方法，二进制格式再加一个字节入口；基础类型 / 字节 / 流 / dynamic 由框架处理。
/// </summary>
public sealed class ContentSerializerTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ToXml(object value, Type type)
    {
        var serializer = new XmlSerializer(type);
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true };
        using var text = new StringWriter();
        using (var writer = XmlWriter.Create(text, settings))
        {
            serializer.Serialize(writer, value);
        }

        return text.ToString();
    }

    private static object? FromXml(string text, Type type)
    {
        var serializer = new XmlSerializer(type);
        using var reader = new StringReader(text);
        return serializer.Deserialize(reader);
    }

    private static TestResponse XmlResponse(object value)
        => new(200, Body: Encoding.UTF8.GetBytes(ToXml(value, value.GetType())), ContentType: "application/xml; charset=utf-8");

    [Fact]
    public async Task XmlSerializer_DrivesObjectRequestBodyAndResponse()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
            ? XmlResponse(new XmlOrder { Id = 7, Name = "book" })
            : TestJson.Echo(req, contentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });

        await client.PostAsync(
            server.Url + "orders",
            new XmlOrder { Id = 1, Name = "pen" },
            c => c.ContentType = AxiosContentType.Xml);

        // 请求：业务对象 → 序列化器 → XML 文本；媒体类型来自 config
        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("<XmlOrder", Body(server.Received[0]));
        Assert.Contains("<Id>1</Id>", Body(server.Received[0]));

        // 响应：业务对象 → 序列化器，返回值原样进 Data
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/1");
        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("book", response.Data.Name);
    }

    [Fact]
    public async Task ScalarResponse_IsHandledByFramework_WithoutTouchingSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("42"),
            ContentType: "text/plain; charset=utf-8"));

        // 序列化器一旦被调用就炸：基础类型走框架 ScalarParser，不该碰到它
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new ThrowingSerializer() });

        Assert.Equal(42, (await client.GetAsync<int>(server.Url + "n")).Data);
        Assert.Equal("42", (await client.GetAsync<string>(server.Url + "n")).Data);
        Assert.Equal(42L, (await client.GetAsync<long>(server.Url + "n")).Data);
    }

    [Fact]
    public async Task NonGenericJsonResponse_ReturnsJsonElement()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 3 }));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });

        // 「无目标类型」是框架形态：JSON 解析成功给 JsonElement，与序列化器是谁无关
        var response = await client.GetAsync(server.Url + "x");
        var element = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal(3, element.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task NonGenericResponse_FallsBackToRawTextWhenNotJson()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 2, Name = "cup" }));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });

        var response = await client.GetAsync(server.Url + "orders/2");
        var text = Assert.IsType<string>(response.Data);
        Assert.Contains("<Name>cup</Name>", text);
    }

    [Fact]
    public async Task FormFlattening_IsIndependentOfSerializer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        // 换成 XML 序列化器后，表单请求仍然正常：表单不走序列化器
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });

        await client.PostAsync(
            server.Url + "form",
            new XmlOrder { Id = 3, Name = "box" },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("Id=3&Name=box", Body(server.Received[0]));
    }

    [Fact]
    public async Task Dynamic_StillRequiresJsonBody()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 4, Name = "pen" }));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });

        // dynamic 是 JSON 便利能力（框架内解析），XML 响应下必然失败；
        // 要拿 XML 原文请显式 ResponseType.Text 或用非泛型 GetAsync()。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<dynamic>(server.Url + "orders/4"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task BinaryResponse_GoesToByteOverload_WithoutTextDecoding()
    {
        // 非 UTF-8 的尾字节 0xFF：只要中途被解码成 string 再编码回去就会变成替换符，
        // 因此「尾部字节还能原样拿到」即证明字节入口没有经过任何文本中转。
        var payload = new byte[] { 7, 0, 0, 0, 2, (byte)'o', (byte)'k', 0xFF };
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: payload,
            ContentType: AxiosContentType.OctetStream));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new BinaryOrderSerializer() });
        var response = await client.GetAsync<BinaryOrder>(server.Url + "orders/7.bin");

        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("ok", response.Data.Name);
        Assert.Equal(0xFF, response.Data.Tail);
    }

    [Fact]
    public async Task TextualResponse_NeverCallsByteOverload()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 9, Name = "ink" }));

        // 字节入口一旦被调用就炸：XML 响应必须走文本入口
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer(bytesThrow: true) });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/9");

        Assert.Equal(9, response.Data!.Id);
    }

    [Fact]
    public async Task BinaryContentType_StillWorksForTextSerializers()
    {
        // 现实里大量服务端把 JSON / XML 标成 application/octet-stream：
        // 接口里字节入口的默认实现按 UTF-8 解码转调文本入口，所以文本序列化器无需实现该方法。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes(ToXml(new XmlOrder { Id = 5, Name = "map" }, typeof(XmlOrder))),
            ContentType: AxiosContentType.OctetStream));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CustomXmlSerializer() });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/5");

        Assert.Equal(5, response.Data!.Id);
        Assert.Equal("map", response.Data.Name);
    }

    [Fact]
    public async Task NonGenericResponse_ReturnsRawBytesForBinaryContent()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF };
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: payload,
            ContentType: "image/png"));

        using var client = new AxiosClient();

        // 没有目标类型时框架自己判形态：二进制给字节，不猜成文本
        var response = await client.GetAsync(server.Url + "logo.png");
        Assert.Equal(payload, Assert.IsType<byte[]>(response.Data));

        // 显式要字节时同样不解码
        var explicitBytes = await client.GetAsync<byte[]>(server.Url + "logo.png");
        Assert.Equal(payload, explicitBytes.Data);
    }

    [Fact]
    public async Task ScalarRequestBody_IsWrittenAsRawText_WithoutTouchingSerializer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        // 序列化器一旦被调用就炸：基础数据类型由框架直接取文本
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new ThrowingSerializer() });

        await client.PostAsync(server.Url + "x", 42);
        Assert.Equal("42", Body(server.Received[0]));
        Assert.Equal("application/json", server.Received[0].Headers["Content-Type"]);

        await client.PostAsync(server.Url + "x", true);
        Assert.Equal("true", Body(server.Received[1]));

        await client.PostAsync(server.Url + "x", 3.5m);
        Assert.Equal("3.5", Body(server.Received[2]));

        await client.PostAsync(server.Url + "x", OrderStatus.Paid);
        Assert.Equal("Paid", Body(server.Received[3]));
    }

    [Fact]
    public async Task ScalarRequestBody_DateTimeKeepsKindAndTicks()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new ThrowingSerializer() });

        // 往返格式：不折成 UTC、不截断到毫秒（表单/查询串的标量规则不复用在请求体上）
        var utc = new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(9);
        await client.PostAsync(server.Url + "x", utc);
        Assert.Equal(utc.ToString("O", CultureInfo.InvariantCulture), Body(server.Received[0]));

        var offset = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        await client.PostAsync(server.Url + "x", offset);
        Assert.Equal(offset.ToString("O", CultureInfo.InvariantCulture), Body(server.Received[1]));
    }

    [Fact]
    public async Task ScalarRequestBody_WorksForNonJsonFormats()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new ThrowingSerializer() });

        // XML / YAML 序列化器对「无名的根标量」本来也无从下手：框架直接写文本，与 ContentType 无关
        await client.PostAsync(server.Url + "x", 42, c => c.ContentType = AxiosContentType.Xml);

        Assert.Equal("42", Body(server.Received[0]));
        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task DictionaryRequestBody_GoesThroughSerializer_LikeAnyBusinessObject()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        var serializer = new RecordingSerializer();
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = serializer });

        await client.PostAsync(server.Url + "x", new Dictionary<string, object?> { ["a"] = 1 });

        // 字典与对象是同一套逻辑：交给序列化器，框架不展平、不当标量
        Assert.Equal(typeof(Dictionary<string, object?>), Assert.Single(serializer.SerializedTypes));
        Assert.Equal("""{"handled":"Dictionary"}""", Body(server.Received[0]));
    }

    [Fact]
    public async Task BuiltInXmlSerializer_RoundTripsObjects_WithoutNamespaceNoise()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
            ? new TestResponse(
                200,
                Body: Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="utf-8"?><XmlOrder><Id>7</Id><Name>书</Name></XmlOrder>"""),
                ContentType: "application/xml; charset=utf-8")
            : TestJson.Echo(req, contentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });

        await client.PostAsync(
            server.Url + "orders",
            new XmlOrder { Id = 1, Name = "笔" },
            c => c.ContentType = AxiosContentType.Xml);

        var body = Body(server.Received[0]);
        Assert.StartsWith("""<?xml version="1.0" encoding="utf-8"?>""", body); // 声明与实际写 socket 的编码一致
        Assert.DoesNotContain("xmlns:xsi", body);                             // 无命名空间噪音
        Assert.Contains("<Id>1</Id>", body);
        Assert.Contains("<Name>笔</Name>", body);                             // 中文按 UTF-8 写入

        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/1");
        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("书", response.Data.Name);
    }

    [Fact]
    public async Task BuiltInXmlSerializer_HonoursDeclaredRootNamespace()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });

        await client.PostAsync(
            server.Url + "x",
            new NamespacedOrder { Id = 3 },
            c => c.ContentType = AxiosContentType.Xml);

        var body = Body(server.Received[0]);
        Assert.Contains("xmlns=\"urn:ax\"", body);
        Assert.DoesNotContain("xmlns:xsi", body);
    }

    [Fact]
    public async Task BuiltInXmlSerializer_RejectsDtd_AsBadResponse()
    {
        // 外部实体是典型的 XXE：内置序列化器禁用 DTD，不能因为「只是解析个响应」就打开文件读取面。
        const string payload = """<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><XmlOrder><Id>1</Id><Name>&xxe;</Name></XmlOrder>""";

        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes(payload),
            ContentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<XmlOrder>(server.Url + "x"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task JsonDocumentTarget_IsSupported_ButMustBeDisposedByTheCaller()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 3 }));
        using var client = new AxiosClient();

        // JsonDocument 可以当泛型目标：它不是「无目标类型」，所以落到序列化器（STJ 原生支持），
        // 代价是由调用方负责 Dispose（JsonDocument 背后有池化缓冲）。
        using var document = (await client.GetAsync<JsonDocument>(server.Url + "x")).Data!;
        Assert.Equal(3, document.RootElement.GetProperty("id").GetInt32());

        // 对照：JsonElement 是框架自己的文档形态，不需要也不允许释放
        var element = (await client.GetAsync<JsonElement>(server.Url + "x")).Data;
        Assert.Equal(3, element.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task JsonDocumentTarget_DependsOnTheActiveSerializer()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 2, Name = "cup" }));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });

        // 换成 XML 序列化器后 JsonDocument 目标没有意义（XmlSerializer 建不出 JsonDocument）
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<JsonDocument>(server.Url + "x"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task SerializerDeclaredMediaType_FillsContentType_WhenCallerDidNotSetIt()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CsvLikeSerializer() });

        // 调用方没设 ContentType：头跟着序列化器自报的媒体类型走
        // （对应 axios 默认 transformRequest 的 headers.setContentType(..., false)）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        Assert.Equal("text/x-test-table", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("Id,Name", Body(server.Received[0]));
    }

    [Fact]
    public async Task ExplicitContentType_WinsOverSerializerDeclaredMediaType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new CsvLikeSerializer() });

        await client.PostAsync(
            server.Url + "x",
            new XmlOrder { Id = 1, Name = "a" },
            c => c.ContentType = "application/vnd.me+table");

        // 调用方显式表态优先：头体怎么配对是他对服务端的判断，库不替他改主意
        Assert.Equal("application/vnd.me+table", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("Id,Name", Body(server.Received[0]));
    }

    [Fact]
    public async Task BuiltInXmlSerializer_FillsContentType_ByItself()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance });

        // 只注入序列化器、不设 ContentType：头自动变成 application/xml
        // （修复前这里会发出「头 application/json、正文 XML」的自相矛盾请求）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 2, Name = "b" });

        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("<Id>2</Id>", Body(server.Received[0]));
    }

    [Fact]
    public async Task SerializerWithoutDeclaredMediaType_KeepsConfiguredContentType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new RecordingSerializer() });

        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        // MediaType 默认 null = 不自报：沿用 config 的 application/json（老实现的零破坏路径）
        Assert.Equal("application/json", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task InstanceDefaultContentType_AlsoWinsOverSerializerDeclaredMediaType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            ContentType = "application/x-team-json",
            ContentSerializer = new CsvLikeSerializer()
        });

        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        // 实例 defaults 里的 Content-Type 也算「调用方已表态」，合并后不能被序列化器改掉
        // （axios 的 setContentType(..., false) 同样不覆盖合并进来的已有头）
        Assert.Equal("application/x-team-json", server.Received[0].Headers["Content-Type"]);
    }

    private enum OrderStatus
    {
        Paid
    }

    /// <summary>XML 文本序列化器：接口只有两个方法。</summary>
    private sealed class CustomXmlSerializer : IContentSerializer
    {
        private readonly bool _bytesThrow;

        public CustomXmlSerializer(bool bytesThrow = false) => _bytesThrow = bytesThrow;

        public string Serialize(object? value, Type type, AxiosConfig config) => ToXml(value!, type);

        public object? Deserialize(string text, Type type, AxiosConfig config) => FromXml(text, type);

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
            => _bytesThrow
                ? throw new InvalidOperationException("文本响应不应进入字节入口。")
                : Deserialize(Encoding.UTF8.GetString(data.Span), type, config);
    }

    /// <summary>记录「谁被序列化了」：用来确认哪些类型会进序列化器。</summary>
    private sealed class RecordingSerializer : IContentSerializer
    {
        public List<Type> SerializedTypes { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            SerializedTypes.Add(type);
            return """{"handled":"Dictionary"}""";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new NotSupportedException();
    }

    /// <summary>二进制格式（这里用「int32 + 长度前缀 + 尾部字节」代替 protobuf）。</summary>
    private sealed class BinaryOrderSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config)
            => throw new NotSupportedException("二进制格式由调用方序列化成 byte[] 后直接作为 data 发送。");

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("二进制响应不应进入文本入口。");

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        {
            var span = data.Span;
            var length = span[4];
            return new BinaryOrder
            {
                Id = BitConverter.ToInt32(span[..4]),
                Name = Encoding.UTF8.GetString(span.Slice(5, length)),
                Tail = span[5 + length]
            };
        }
    }

    [Fact]
    public async Task Serializer_CanWriteRequestHeaders_WithoutPollutingInstanceDefaults()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new HeaderWritingSerializer() });

        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        // 序列化器拿到的 config 是 MergeConfig 里 Defaults.Clone() 出来的 per-request 副本
        // （Clone 会 _headers?.Clone()），所以写进去的头能生效、且不会漏到下一请求
        Assert.Equal("application/x-from-serializer", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("csv", server.Received[0].Headers["X-Body-Format"]);
        Assert.Equal("v1", server.Received[0].Headers["X-Body-Version"]);

        Assert.False(client.Defaults.Headers.Has("X-Body-Format"));
        Assert.False(client.Defaults.Headers.Has("X-Body-Version"));
    }

    [Fact]
    public async Task Deserialize_CanReadResponseHeaders()
    {
        await using var server = TestHttpServer.Start(req => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, string>
            {
                ["X-Schema-Version"] = req.Path.EndsWith("/v2") ? "2" : "1"
            }));

        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new VersionAwareSerializer() });

        // 同一份 DTO、同样的响应体，仅凭响应头选择解析规则
        var v1 = await client.GetAsync<XmlOrder>(server.Url + "v1");
        var v2 = await client.GetAsync<XmlOrder>(server.Url + "v2");

        Assert.Equal(100, v1.Data!.Id);
        Assert.Equal(200, v2.Data!.Id);

        // 与 AxiosResponse.Headers 是同一份数据，不用二次解析
        Assert.Equal("2", v2.Config!.Response!.GetHeader("X-Schema-Version"));
        Assert.Equal(200, v2.Config.Response.Status);
    }

    [Fact]
    public async Task Serialize_SeesNoResponseContext()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new VersionAwareSerializer() });

        // 请求阶段 config.Response 必须是 null（夹具见到非 null 就抛，会变成 ERR_BAD_REQUEST）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" }, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("{}", Body(server.Received[0]));
    }

    [Fact]
    public async Task TransformResponse_CanReadResponseHeaders()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("""{"id":7}"""),
            Headers: new Dictionary<string, string> { ["X-Total-Count"] = "42" }));

        string? total = null;
        var config = new AxiosConfig();
        config.TransformResponse.Add((data, cfg) =>
        {
            // 反序列化已完成，头仍然在：适合把元数据贴到业务对象上
            total = cfg.Response?.GetHeader("X-Total-Count");
            return data;
        });

        using var client = new AxiosClient(config);
        var response = await client.GetAsync<XmlOrder>(server.Url + "x");

        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("42", total);
    }

    [Fact]
    public async Task ResponseContext_DoesNotSurviveConfigClone()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("""{"id":1}""")));

        using var client = new AxiosClient();
        var response = await client.GetAsync<XmlOrder>(server.Url + "x");

        Assert.NotNull(response.Config!.Response);
        Assert.Equal("application/json", response.Config.Response.MediaType);

        // 克隆出的配置是给下一次请求用的：带着上一次的响应头会让序列化器读到过期状态
        Assert.Null(response.Config.Clone().Response);
    }

    /// <summary>会自报媒体类型的文本格式（拿 CSV 形状代替 YAML / XML 这类文本格式）。</summary>
    private sealed class CsvLikeSerializer : IContentSerializer
    {
        public string? MediaType => "text/x-test-table";

        public string Serialize(object? value, Type type, AxiosConfig config) => "Id,Name";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>
    /// 在序列化时顺带写请求头（对齐 axios `transformRequest(data, headers)` 里 setContentType 的用法）：
    /// 走 <see cref="AxiosConfig.Headers"/>，由库在请求体构造之后统一应用。
    /// </summary>
    private sealed class HeaderWritingSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            config.Headers.Set("Content-Type", "application/x-from-serializer");
            config.Headers.Set("X-Body-Format", "csv");
            config.Headers.Set("X-Body-Version", "v1");
            return "Id,Name";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>按响应头 <c>X-Schema-Version</c> 选择解析规则的序列化器（响应头可见性用例）。</summary>
    private sealed class VersionAwareSerializer : IContentSerializer
    {
        public string? MediaType => "application/x-versioned";

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            if (config.Response is not null)
            {
                throw new InvalidOperationException("请求阶段不该有响应上下文。");
            }

            return "{}";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => config.Response?.GetHeader("X-Schema-Version") == "2"
                ? new XmlOrder { Id = 200, Name = "v2" }
                : new XmlOrder { Id = 100, Name = "v1" };
    }

    /// <summary>一旦被调用就失败：用来证明某些路径根本不经过序列化器。</summary>
    private sealed class ThrowingSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config)
            => throw new InvalidOperationException("序列化器不应被调用。");

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("序列化器不应被调用。");
    }
}
