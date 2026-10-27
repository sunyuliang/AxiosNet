
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Serialization;
using System.Xml;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 序列化：JSON/XML 内置实现、字节通道、标量直出、上下文头读写、字符集声明。
/// </summary>
public sealed class SerializerTests
{
    // ===============================================================
    // Bug 2：内置 JSON 序列化器字节入口不剥 UTF-8 BOM
    // ===============================================================

    [Fact]
    public async Task BuiltInJsonSerializer_ByteEntry_StripsUtf8Bom()
    {
        // 服务端把 JSON 标成 application/octet-stream（非文本 → 走字节入口）且正文带 UTF-8 BOM
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"bom\",\"age\":7}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(ContentType: "application/octet-stream", Body: body));

        using var client = new AxiosClient();

        var response = await client.RequestAsync<Person>(new AxiosConfig { Url = server.Url + "x" });

        Assert.Equal("bom", response.Data!.Name);
        Assert.Equal(7, response.Data.Age);
    }

    [Fact]
    public async Task BuiltInJsonSerializer_TextEntry_StillDecodesBomAsBefore()
    {
        // 文本路径（对照组）：解码时剥 BOM 的行为不变
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"bom\",\"age\":7}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body));

        using var client = new AxiosClient();

        var response = await client.RequestAsync<Person>(new AxiosConfig { Url = server.Url + "x" });

        Assert.Equal("bom", response.Data!.Name);
    }

    // ===============================================================
    // Bug 5：自定义序列化器抛 OperationCanceledException 被误报成解析失败
    // ===============================================================

    [Fact]
    public async Task CustomSerializer_TextEntry_OperationCanceled_PropagatesUnwrapped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new()
            {
                ContentSerializer = new CancelingSerializer(),
            },
        });

        // 取消语义必须归一为 ERR_CANCELED（外层管线对 OCE 的统一包装），
        // 不能在下层被包装成 ERR_BAD_RESPONSE —— 那会把「取消」误报成「解析失败」
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync<Person>(new AxiosConfig { Url = "x" }));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsType<OperationCanceledException>(ex.Cause);
    }

    [Fact]
    public async Task CustomSerializer_ByteEntry_OperationCanceled_PropagatesUnwrapped()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            ContentType: "application/octet-stream",
            Body: Encoding.UTF8.GetBytes("{}")));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new()
            {
                ContentSerializer = new CancelingSerializer(),
            },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync<Person>(new AxiosConfig { Url = "x" }));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsType<OperationCanceledException>(ex.Cause);
    }

    // ------------------------------------------------------------------
    // 7) 标量请求体的头必须跟着生效序列化器。
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScalarBody_TakesMediaTypeFromInjectedSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "x",
            42,
            c => c.Serialization.ContentSerializer = AxiosNet.Serialization.XmlContentSerializer.Instance);

        var received = Assert.Single(server.Received);
        Assert.StartsWith(AxiosContentType.Xml, received.Headers["Content-Type"]);
        Assert.Equal("42", Encoding.UTF8.GetString(received.Body));
    }

    // ===============================================================
    // L12：JSON 字节入口要和处理文本路径一样认 UTF-16 / UTF-32 BOM
    // ===============================================================

    [Fact]
    public async Task L12_JsonByteEntry_Utf16Bom_DecodesLikeTextPath()
    {
        var json = "{\"name\":\"héllo\",\"age\":3}";
        var body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(json))
            .ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body, ContentType: "application/octet-stream"));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync<Person>("x");

        Assert.Equal("héllo", response.Data!.Name);
        Assert.Equal(3, response.Data.Age);
    }

    // ------------------------------------------------------------------
    // Bug 1：非文本 Content-Type + 声明 charset
    // ------------------------------------------------------------------

    [Fact]
    public async Task TypedJson_BinaryResponseWithDeclaredCharset_DecodesWithThatCharset()
    {
        // UTF-16LE、无 BOM：字节入口只有真正按 charset 解码才可能读对。
        var body = Encoding.Unicode.GetBytes("{\"name\":\"测试\"}");
        using var client = CreateClient(body, contentType: "application/octet-stream; charset=utf-16");

        var response = await client.GetAsync<Payload>("payload");

        Assert.Equal("测试", response.Data!.Name);
    }

    [Fact]
    public async Task TypedJson_BinaryResponseWithForcedResponseEncoding_UsesForcedEncoding()
    {
        var body = Encoding.Unicode.GetBytes("{\"name\":\"强编码\"}");
        using var client = CreateClient(body, contentType: "application/octet-stream");

        var response = await client.GetAsync<Payload>(
            "payload",
            config => config.Serialization.ResponseEncoding = Encoding.Unicode);

        Assert.Equal("强编码", response.Data!.Name);
    }

    // ---------------------------------------------------------------
    // 16：charset 收口（序列化器自报 / 写头都覆盖）
    // ---------------------------------------------------------------

    [Fact]
    public async Task SerializerDeclaredCharset_ContradictingBodyEncoding_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 自报的 MediaType 带 charset=iso-8859-1，而正文按 UTF-8 编码：入口预校验看不到
        // 序列化器的自报值（它发生在正文产出阶段），曾静默发出「头 latin1、正文 utf-8」。
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new CharsetDeclaringSerializer() },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", new { a = 1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("charset=iso-8859-1", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task SerializerWrittenCharsetHeader_ContradictingBodyEncoding_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 序列化器在 Serialize 里写请求头：同样在正文产出之后才可见
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new HeaderWritingCharsetSerializer() },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", new { a = 1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("charset=iso-8859-1", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task SerializerDeclaredCharset_MatchingBodyEncoding_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 自报与正文一致的 charset：放行，且头里保留该声明
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new Utf8CharsetDeclaringSerializer() },
        });

        await client.PostAsync("upload", new { a = 1 });

        Assert.Contains("charset=utf-8", Assert.Single(server.Received).Headers["Content-Type"]);
    }

    [Fact]
    public async Task XmlSerializer_DrivesObjectRequestBodyAndResponse()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
            ? XmlResponse(new XmlOrder { Id = 7, Name = "book" })
            : TestJson.Echo(req, contentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });

        await client.PostAsync(
            server.Url + "orders",
            new XmlOrder { Id = 1, Name = "pen" },
            c => c.ContentType = AxiosContentType.Xml);

        // 请求：业务对象 → 序列化器 → XML 文本；媒体类型来自 config
        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("<XmlOrder", Body_ContentSerializer(server.Received[0]));
        Assert.Contains("<Id>1</Id>", Body_ContentSerializer(server.Received[0]));

        // 响应：业务对象 → 序列化器，返回值原样进 Data
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/1");
        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("book", response.Data.Name);
    }

    [Fact]
    public async Task ScalarResponse_IsHandledByFramework_WithoutTouchingSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: Encoding.UTF8.GetBytes("42"), ContentType: "text/plain; charset=utf-8"));

        // 序列化器一旦被调用就炸：基础类型走框架 ScalarParser，不该碰到它
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new ThrowingSerializer(),
            },
        });

        Assert.Equal(42, (await client.GetAsync<int>(server.Url + "n")).Data);
        Assert.Equal("42", (await client.GetAsync<string>(server.Url + "n")).Data);
        Assert.Equal(42L, (await client.GetAsync<long>(server.Url + "n")).Data);
    }

    [Fact]
    public async Task NonGenericJsonResponse_ReturnsJsonElement()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 3 }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });

        // 「无目标类型」是框架形态：JSON 解析成功给 JsonElement，与序列化器是谁无关
        var response = await client.GetAsync(server.Url + "x");
        var element = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal(3, element.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task NonGenericResponse_FallsBackToRawTextWhenNotJson()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 2, Name = "cup" }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });

        var response = await client.GetAsync(server.Url + "orders/2");
        var text = Assert.IsType<string>(response.Data);
        Assert.Contains("<Name>cup</Name>", text);
    }

    [Fact]
    public async Task FormFlattening_IsIndependentOfSerializer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        // 换成 XML 序列化器后，表单请求仍然正常：表单不走序列化器
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });

        await client.PostAsync(
            server.Url + "form",
            new XmlOrder { Id = 3, Name = "box" },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("Id=3&Name=box", Body_ContentSerializer(server.Received[0]));
    }

    [Fact]
    public async Task Dynamic_StillRequiresJsonBody()
    {
        await using var server = TestHttpServer.Start(_ => XmlResponse(new XmlOrder { Id = 4, Name = "pen" }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });

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
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: payload, ContentType: AxiosContentType.OctetStream));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new BinaryOrderSerializer(),
            },
        });
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
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(bytesThrow: true),
            },
        });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/9");

        Assert.Equal(9, response.Data!.Id);
    }

    [Fact]
    public async Task BinaryContentType_StillWorksForTextSerializers()
    {
        // 现实里大量服务端把 JSON / XML 标成 application/octet-stream：
        // 接口里字节入口的默认实现按响应字符集解码转调文本入口，所以文本序列化器无需实现该方法。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes(ToXml(new XmlOrder { Id = 5, Name = "map" }, typeof(XmlOrder))),
            ContentType: AxiosContentType.OctetStream));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CustomXmlSerializer(),
            },
        });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/5");

        Assert.Equal(5, response.Data!.Id);
        Assert.Equal("map", response.Data.Name);
    }

    [Fact]
    public async Task ByteOverloadDefault_FollowsResponseCharset()
    {
        // 错标成 octet-stream 且带 charset 的非 UTF-8 文本：DIM 默认实现与文本响应共用同一套解码优先级。
        // Latin1 的 0xE9 在写死 UTF-8 下会变成 U+FFFD，因此断言原文即证明 charset 生效。
        var latin1 = Encoding.Latin1.GetBytes(ToXml(new XmlOrder { Id = 6, Name = "café" }, typeof(XmlOrder)));
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: latin1, ContentType: "application/octet-stream; charset=iso-8859-1"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new PlainXmlSerializer(),
            },
        });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/6");

        Assert.Equal(6, response.Data!.Id);
        Assert.Equal("café", response.Data.Name);
    }

    [Fact]
    public async Task ByteOverloadDefault_DetectsBom_WhenCharsetMissing()
    {
        // 没有 charset：DIM 按 BOM 探测（UTF-16LE，需手工带上 BOM 前缀），与文本响应同规则
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(ToXml(new XmlOrder { Id = 7, Name = "bom" }, typeof(XmlOrder)))).ToArray();
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: utf16, ContentType: AxiosContentType.OctetStream));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new PlainXmlSerializer(),
            },
        });
        var response = await client.GetAsync<XmlOrder>(server.Url + "orders/7");

        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("bom", response.Data.Name);
    }

    [Fact]
    public async Task BinarySerializer_ObjectRequest_GoesThroughByteChannel_WithSelfReportedContentType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: AxiosContentType.OctetStream));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new BinaryOrderSerializer(),
            },
        });

        // RequestEncoding 只作用于文本通道：字节正文不做编码，Content-Type 也不补 charset
        var response = await client.PostAsync<BinaryOrder>(
            server.Url + "echo",
            new BinaryOrder { Id = 3, Name = "cat", Tail = 0xAB },
            c => c.Serialization.RequestEncoding = Encoding.UTF32);

        var sent = server.Received[0];
        Assert.Equal(BinaryOrderSerializer.Write(new BinaryOrder { Id = 3, Name = "cat", Tail = 0xAB }), sent.Body);
        Assert.Equal(AxiosContentType.OctetStream, sent.Headers["Content-Type"]);

        // 回显响应同样是 octet-stream → 字节入口解析回对象：一个序列化器覆盖请求与响应两个方向
        Assert.Equal(3, response.Data!.Id);
        Assert.Equal("cat", response.Data.Name);
        Assert.Equal(0xAB, response.Data.Tail);
    }

    [Fact]
    public async Task ByteChannel_ExplicitContentType_WinsOverSelfReported()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "application/x-custom"));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new BinaryOrderSerializer(),
            },
        });

        await client.PostAsync(
            server.Url + "echo",
            new BinaryOrder { Id = 1, Name = "x", Tail = 1 },
            c => c.ContentType = "application/x-custom");

        // 正文仍是序列化器产出的字节，但头以调用方为准（与文本通道的 ContentTypeSet 优先级一致）
        Assert.Equal("application/x-custom", server.Received[0].Headers["Content-Type"]);
        Assert.Equal(BinaryOrderSerializer.Write(new BinaryOrder { Id = 1, Name = "x", Tail = 1 }), server.Received[0].Body);
    }

    [Fact]
    public async Task ByteChannel_NotUsedForFormMultipartAndScalars()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        // SerializeToBytes 对一切都返回字节：只要表单 / 标量误入序列化器，正文就会变成 1,2,3
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new GreedyByteSerializer(),
            },
        });

        await client.PostAsync(server.Url + "form", new Dictionary<string, object?> { ["a"] = 1 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);
        Assert.Equal("a=1", Body_ContentSerializer(server.Received[0]));

        await client.PostAsync(server.Url + "scalar", 42);
        Assert.Equal("42", Body_ContentSerializer(server.Received[1]));
    }

    [Fact]
    public async Task NonGenericResponse_ReturnsRawBytesForBinaryContent()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF };
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: payload, ContentType: "image/png"));

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
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new ThrowingSerializer(),
            },
        });

        await client.PostAsync(server.Url + "x", 42);
        Assert.Equal("42", Body_ContentSerializer(server.Received[0]));
        Assert.Equal("application/json", server.Received[0].Headers["Content-Type"]);

        await client.PostAsync(server.Url + "x", true);
        Assert.Equal("true", Body_ContentSerializer(server.Received[1]));

        await client.PostAsync(server.Url + "x", 3.5m);
        Assert.Equal("3.5", Body_ContentSerializer(server.Received[2]));

        await client.PostAsync(server.Url + "x", OrderStatus.Paid);
        Assert.Equal("Paid", Body_ContentSerializer(server.Received[3]));
    }

    [Fact]
    public async Task ScalarRequestBody_DateTimeKeepsKindAndTicks()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new ThrowingSerializer(),
            },
        });

        // 往返格式：不折成 UTC、不截断到毫秒（表单/查询串的标量规则不复用在请求体上）
        var utc = new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(9);
        await client.PostAsync(server.Url + "x", utc);
        Assert.Equal(utc.ToString("O", CultureInfo.InvariantCulture), Body_ContentSerializer(server.Received[0]));

        var offset = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        await client.PostAsync(server.Url + "x", offset);
        Assert.Equal(offset.ToString("O", CultureInfo.InvariantCulture), Body_ContentSerializer(server.Received[1]));
    }

    [Fact]
    public async Task ScalarRequestBody_WorksForNonJsonFormats()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new ThrowingSerializer(),
            },
        });

        // XML / YAML 序列化器对「无名的根标量」本来也无从下手：框架直接写文本，与 ContentType 无关
        await client.PostAsync(server.Url + "x", 42, c => c.ContentType = AxiosContentType.Xml);

        Assert.Equal("42", Body_ContentSerializer(server.Received[0]));
        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task DictionaryRequestBody_GoesThroughSerializer_LikeAnyBusinessObject()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        var serializer = new RecordingSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = serializer,
            },
        });

        await client.PostAsync(server.Url + "x", new Dictionary<string, object?> { ["a"] = 1 });

        // 字典与对象是同一套逻辑：交给序列化器，框架不展平、不当标量
        Assert.Equal(typeof(Dictionary<string, object?>), Assert.Single(serializer.SerializedTypes));
        Assert.Equal("""{"handled":"Dictionary"}""", Body_ContentSerializer(server.Received[0]));
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

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        await client.PostAsync(
            server.Url + "orders",
            new XmlOrder { Id = 1, Name = "笔" },
            c => c.ContentType = AxiosContentType.Xml);

        var body = Body_ContentSerializer(server.Received[0]);
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
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        await client.PostAsync(
            server.Url + "x",
            new NamespacedOrder { Id = 3 },
            c => c.ContentType = AxiosContentType.Xml);

        var body = Body_ContentSerializer(server.Received[0]);
        Assert.Contains("xmlns=\"urn:ax\"", body);
        Assert.DoesNotContain("xmlns:xsi", body);
    }

    [Fact]
    public async Task BuiltInXmlSerializer_RejectsDtd_AsBadResponse()
    {
        // 外部实体是典型的 XXE：内置序列化器禁用 DTD，不能因为「只是解析个响应」就打开文件读取面。
        const string payload = """<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><XmlOrder><Id>1</Id><Name>&xxe;</Name></XmlOrder>""";

        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: Encoding.UTF8.GetBytes(payload), ContentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

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
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        // 换成 XML 序列化器后 JsonDocument 目标没有意义（XmlSerializer 建不出 JsonDocument）
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<JsonDocument>(server.Url + "x"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task SerializerDeclaredMediaType_FillsContentType_WhenCallerDidNotSetIt()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CsvLikeSerializer(),
            },
        });

        // 调用方没设 ContentType：头跟着序列化器自报的媒体类型走
        // （对应 axios 默认 transformRequest 的 headers.setContentType(..., false)）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        Assert.Equal("text/x-test-table", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("Id,Name", Body_ContentSerializer(server.Received[0]));
    }

    [Fact]
    public async Task ExplicitContentType_WinsOverSerializerDeclaredMediaType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new CsvLikeSerializer(),
            },
        });

        await client.PostAsync(
            server.Url + "x",
            new XmlOrder { Id = 1, Name = "a" },
            c => c.ContentType = "application/vnd.me+table");

        // 调用方显式表态优先：头体怎么配对是他对服务端的判断，库不替他改主意
        Assert.Equal("application/vnd.me+table", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("Id,Name", Body_ContentSerializer(server.Received[0]));
    }

    [Fact]
    public async Task BuiltInXmlSerializer_FillsContentType_ByItself()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        // 只注入序列化器、不设 ContentType：头自动变成 application/xml
        // （不能发出「头 application/json、正文 XML」的自相矛盾请求）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 2, Name = "b" });

        Assert.Equal("application/xml", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("<Id>2</Id>", Body_ContentSerializer(server.Received[0]));
    }

    [Fact]
    public async Task SerializerWithoutDeclaredMediaType_KeepsConfiguredContentType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new RecordingSerializer(),
            },
        });

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
            Serialization = new()
            {
                ContentSerializer = new CsvLikeSerializer(),
            },
        });

        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" });

        // 实例 defaults 里的 Content-Type 也算「调用方已表态」，合并后不能被序列化器改掉
        // （axios 的 setContentType(..., false) 同样不覆盖合并进来的已有头）
        Assert.Equal("application/x-team-json", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task Serializer_CanWriteRequestHeaders_WithoutPollutingInstanceDefaults()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new HeaderWritingSerializer(),
            },
        });

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

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new VersionAwareSerializer(),
            },
        });

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
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = new VersionAwareSerializer(),
            },
        });

        // 请求阶段 config.Response 必须是 null（夹具见到非 null 就抛，会变成 ERR_BAD_REQUEST）
        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "a" }, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("{}", Body_ContentSerializer(server.Received[0]));
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

    [Fact]
    public async Task CustomSerializer_DrivesRequestAndResponse()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/users/1")
            ? TestJson.Response(new { user_name = "bob", age = 30 })
            : TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        var sent = Body_JsonCustomization(server.Received[0]);
        Assert.Contains("\"user_name\":\"alice\"", sent);
        Assert.DoesNotContain("UserName", sent);

        var response = await client.GetAsync<User>(server.Url + "users/1");
        Assert.Equal("bob", response.Data!.UserName);
        Assert.Equal(30, response.Data.Age);
    }

    [Fact]
    public async Task CustomSerializer_DrivesDynamicResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { user_name = "bob", age = 30 }));
        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        dynamic data = (await client.GetAsync<dynamic>(server.Url + "users/1")).Data!;

        Assert.Equal("bob", (string)data.user_name);
        Assert.Equal(30, (int)data.age);
    }

    [Fact]
    public async Task InstanceJsonOptions_OverrideGlobalNaming()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = SnakeCase
            },
            },
        });

        await client.PostAsync(server.Url + "users", new User { UserName = "alice", Age = 20 });

        Assert.Contains("\"user_name\":\"alice\"", Body_JsonCustomization(server.Received[0]));
    }

    [Fact]
    public void GlobalJsonDefaults_AreSystemTextJsonWeb()
    {
        // 未注入任何序列化器：全局槽是 null（= 自动模式，查表未命中才回落内置 STJ）
        Assert.Null(AxiosContent.Serializer);

        // 内置 STJ 的默认值就是 Web defaults：四个维度一起钉住，避免只改其中一项被漏掉
        var options = AxiosJson.Options;
        Assert.Same(JsonNamingPolicy.CamelCase, options.PropertyNamingPolicy);
        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.Equal(JsonNumberHandling.AllowReadingFromString, options.NumberHandling);
        Assert.Equal(JsonIgnoreCondition.Never, options.DefaultIgnoreCondition);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================

    /// <summary>Deserialize 一律抛 OperationCanceledException 的序列化器（遵循取消的实现）。</summary>
    private sealed class CancelingSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new OperationCanceledException();

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
            => throw new OperationCanceledException();
    }


    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------


    private static async Task DrainAsync<T>(IAsyncEnumerable<T>? source)
    {
        if (source is null)
        {
            return;
        }

        await foreach (var _ in source.ConfigureAwait(false))
        {
        }
    }

    private static int ActiveLeases(HttpConnectionPool pool)
        => pool.GetStatistics().Endpoints.Sum(e => e.ActiveLeases);


    /// <summary>每次都返回同一段 NDJSON 正文的处理器。</summary>
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FixedBodyHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(_body))
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return Task.FromResult(response);
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static AxiosClient CreateClient(byte[] body, string contentType)
        => new(
            new AxiosConfig { BaseAddress = "http://audit.test/" },
            handler: new StaticHandler(body, contentType),
            disposeHandler: true);


    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public StaticHandler(byte[] body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }


    /// <summary>每次 Read 只返回一个预置分块，用来把 CRLF 拆到两次读取里。</summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _index;
        private int _offset;

        public ChunkedReadStream(byte[][] chunks) => _chunks = chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            while (_index < _chunks.Length)
            {
                var chunk = _chunks[_index];
                if (_offset < chunk.Length)
                {
                    var count = Math.Min(chunk.Length - _offset, destination.Length);
                    chunk.AsSpan(_offset, count).CopyTo(destination);
                    _offset += count;
                    return count;
                }

                _index++;
                _offset = 0;
            }

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class Payload
    {
        public string? Name { get; set; }
    }


    // ---------------------------------------------------------------
    // 测试模型与序列化器夹具
    // ---------------------------------------------------------------


    /// <summary>自报带 charset 的媒体类型（与正文实际编码矛盾）。</summary>
    private sealed class CharsetDeclaringSerializer : IContentSerializer
    {
        public string? MediaType => "application/json; charset=iso-8859-1";

        public string Serialize(object? value, Type type, AxiosConfig config) => """{"a":1}""";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>自报与正文一致的 charset（utf-8）。</summary>
    private sealed class Utf8CharsetDeclaringSerializer : IContentSerializer
    {
        public string? MediaType => "application/json; charset=utf-8";

        public string Serialize(object? value, Type type, AxiosConfig config) => """{"a":1}""";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>在 Serialize 里写带 charset 的 Content-Type 请求头。</summary>
    private sealed class HeaderWritingCharsetSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            config.Headers.Set("Content-Type", "application/json; charset=iso-8859-1");
            return """{"a":1}""";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    private static string Body_ContentSerializer(TestRequest request) => Encoding.UTF8.GetString(request.Body);

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

    private enum OrderStatus
    {
        Paid
    }

    /// <summary>XML 文本序列化器：不覆写字节入口，专门用来测接口 DIM 默认实现的解码行为。</summary>
    private sealed class PlainXmlSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => ToXml(value!, type);

        public object? Deserialize(string text, Type type, AxiosConfig config) => FromXml(text, type);
    }

    /// <summary>XML 文本序列化器：可注入「字节入口抛异常」用于验证文本响应的路由。</summary>
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

    /// <summary>二进制格式（这里用「int32 + 长度前缀 + 尾部字节」代替 protobuf）：请求出口 + 响应入口都是字节。</summary>
    private sealed class BinaryOrderSerializer : IContentSerializer
    {
        public string? MediaType => AxiosContentType.OctetStream;

        public string Serialize(object? value, Type type, AxiosConfig config)
            => throw new NotSupportedException("二进制格式不支持文本序列化。");

        public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config)
            => value is BinaryOrder order ? Write(order) : null;

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

        internal static byte[] Write(BinaryOrder order)
        {
            var name = Encoding.UTF8.GetBytes(order.Name);
            var buffer = new byte[5 + name.Length + 1];
            BitConverter.GetBytes(order.Id).CopyTo(buffer, 0);
            buffer[4] = (byte)name.Length;
            name.CopyTo(buffer, 5);
            buffer[5 + name.Length] = order.Tail;
            return buffer;
        }
    }

    /// <summary>对一切结构化数据都产出固定字节：用来证明表单 / multipart / 标量不会进入序列化器。</summary>
    private sealed class GreedyByteSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => throw new NotSupportedException();

        public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config) => new byte[] { 1, 2, 3 };

        public object? Deserialize(string text, Type type, AxiosConfig config) => throw new NotSupportedException();
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

    private static string Body_JsonCustomization(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private sealed class User
    {
        public string UserName { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    /// <summary>
    /// net8.0 内置 <c>JsonNamingPolicy.SnakeCaseLower</c>，net6.0 的 System.Text.Json 6 没有，
    /// 这里给出等价实现，保证两个 TFM 的用例语义一致。
    /// </summary>
    private static JsonNamingPolicy SnakeCase =>
#if NET8_0_OR_GREATER
        JsonNamingPolicy.SnakeCaseLower;

#else
        SnakeCaseNamingPolicy.Instance;

#endif

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public static SnakeCaseNamingPolicy Instance { get; } = new();

        public override string ConvertName(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>用 snake_case 固定策略的 STJ 适配器，证明自定义序列化器会绕开 config 里的 options。</summary>
    private sealed class SnakeCaseSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = SnakeCase
        };

        public static SnakeCaseSerializer Instance { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string json, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(json, type, Options);
    }
}