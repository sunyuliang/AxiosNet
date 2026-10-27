
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 序列化器选型与注册表：显式 -> 全局 -> 注册表（别名/后缀）-> 内置 JSON。
/// </summary>
public sealed class SerializerRegistryTests
{
    // ------------------------------------------------------------------
    // 第三方 MediaType 的错误分类 + 错误体 charset
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThirdPartySerializer_MediaTypeWithControlChars_FailsBadOptionValue()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);

        // 第三方序列化器自报的 MediaType 是不可信输入：CR/LF 属于「声明写错了」，
        // 落 ERR_BAD_OPTION_VALUE，不能被收成 ERR_BAD_REQUEST「请求体序列化失败」。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/a",
            new { x = 1 },
            c => c.Serialization.ContentSerializer = new HostileMediaTypeSerializer()));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task AutoMode_UsesRegisteredSerializer_ForRequestAndResponse()
    {
        AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
        try
        {
            await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
                ? new TestResponse(200, Body: Encoding.UTF8.GetBytes("<XmlOrder><Id>7</Id><Name>book</Name></XmlOrder>"), ContentType: "application/xml; charset=utf-8")
                : TestJson.Echo(req, contentType: "application/xml; charset=utf-8"));

            // 不注入任何序列化器：请求 ContentType 与响应 Content-Type 都是 application/xml →
            // 自动模式命中注册表，请求体被序列化成 XML、响应体被反序列化成对象
            using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

            await client.PostAsync(
                "orders",
                new XmlOrder { Id = 1, Name = "pen" },
                c => c.ContentType = AxiosContentType.Xml);

            Assert.Contains("<XmlOrder", Body(server.Received[0]));
            Assert.Contains("<Id>1</Id>", Body(server.Received[0]));

            var response = await client.GetAsync<XmlOrder>("orders/1");
            Assert.Equal(7, response.Data!.Id);
            Assert.Equal("book", response.Data.Name);
        }
        finally
        {
            AxiosContent.Unregister(AxiosContentType.Xml);
        }
    }

    [Fact]
    public async Task AutoMode_FallsBackToBuiltInJson_WhenNothingRegistered()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 未注入 = 自动模式；查表未命中 → 内置 System.Text.Json（camelCase）
        Assert.Null(AxiosContent.Serializer);

        await client.PostAsync(server.Url + "x", new XmlOrder { Id = 1, Name = "pen" });

        Assert.Equal("""{"id":1,"name":"pen"}""", Body(server.Received[0]));
    }

    [Fact]
    public async Task ExplicitInstanceInjection_BeatsRegistry()
    {
        RegisterForTest("application/x-test-xml", XmlContentSerializer.Instance);
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient(new AxiosConfig
{
                Serialization = new()
                {
                    ContentSerializer = new MarkerSerializer("injected"),
                },
            });

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "application/x-test-xml");

            // 显式注入的优先级高于注册表
            Assert.Equal("injected", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Unregister("application/x-test-xml");
        }
    }

    [Fact]
    public async Task SuffixFallback_RoutesStructuredVendorTypes()
    {
        // application/vnd.axios+x-test-fmt → 回落到 application/x-test-fmt（RFC 6839 结构化后缀）
        RegisterForTest("application/x-test-fmt", new MarkerSerializer("by-suffix"));
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "application/vnd.axios+x-test-fmt");

            Assert.Equal("by-suffix", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Unregister("application/x-test-fmt");
        }
    }

    [Fact]
    public async Task ExactRegistration_WinsOverSuffixFallback()
    {
        RegisterForTest("application/x-test-fmt", new MarkerSerializer("by-suffix"));
        RegisterForTest("application/vnd.axios+x-test-fmt", new MarkerSerializer("by-exact"));
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "application/vnd.axios+x-test-fmt");

            Assert.Equal("by-exact", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Unregister("application/x-test-fmt");
            AxiosContent.Unregister("application/vnd.axios+x-test-fmt");
        }
    }

    [Fact]
    public async Task MediaTypeCaseAndParameters_AreIgnored()
    {
        RegisterForTest("APPLICATION/X-Test-Mixed; charset=utf-8", new MarkerSerializer("normalized"));
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "application/x-test-mixed");

            Assert.Equal("normalized", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Unregister("application/x-test-mixed");
        }
    }

    // ---------------------------------------------------------------
    // 同一格式的常见别名：注册 application/xml 后，老服务端写 text/xml 也要命中
    // ---------------------------------------------------------------

    [Fact]
    public async Task TextXmlAlias_FallsBackToRegisteredApplicationXml_ForResponse()
    {
        AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
        try
        {
            await using var server = TestHttpServer.Start(_ => new TestResponse(
                200,
                Body: Encoding.UTF8.GetBytes("<XmlOrder><Id>7</Id><Name>book</Name></XmlOrder>"),
                ContentType: "text/xml; charset=utf-8"));

            using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

            // 只注册了 application/xml，响应头是 text/xml（大量老服务端如此）
            var response = await client.GetAsync<XmlOrder>("orders/1");

            Assert.Equal(7, response.Data!.Id);
            Assert.Equal("book", response.Data.Name);
        }
        finally
        {
            AxiosContent.Unregister(AxiosContentType.Xml);
        }
    }

    [Fact]
    public async Task TextXmlAlias_FallsBackToRegisteredApplicationXml_ForRequest()
    {
        RegisterForTest(AxiosContentType.Xml, new MarkerSerializer("by-text-xml-alias"));
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "text/xml");

            // 请求头保持 text/xml（调用方显式设置优先），正文由注册在 application/xml 上的序列化器产出
            Assert.Equal("by-text-xml-alias", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Unregister(AxiosContentType.Xml);
        }
    }

    [Fact]
    public void Register_ValidatesArguments()
    {        Assert.Throws<ArgumentNullException>(() => AxiosContent.Register("application/x-test-null", null!));
        Assert.Throws<ArgumentException>(() => AxiosContent.Register("   ", new MarkerSerializer("x")));
        Assert.Throws<ArgumentException>(() => AxiosContent.Register("; charset=utf-8", new MarkerSerializer("x")));
        Assert.False(AxiosContent.Unregister("application/x-never-registered"));
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private static HttpResponseMessage JsonResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestJson.Bytes(new Person("alice", 1)))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
            }
        };


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    /// <summary>自报带 CR/LF 的 MediaType 的第三方序列化器（不可信输入样例）。</summary>
    private sealed class HostileMediaTypeSerializer : IContentSerializer
    {
        // MediaType 是默认接口实现：类里重新实现即可（不能用 override）。
        public string? MediaType => "application/json\r\nX-Injected: evil";

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config) => null;
    }


    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static void RegisterForTest(string mediaType, IContentSerializer serializer)
        => AxiosContent.Register(mediaType, serializer);

    /// <summary>只回一个标记串：用来判断「到底是哪个序列化器被调用了」。</summary>
    private sealed class MarkerSerializer : IContentSerializer
    {
        private readonly string _marker;

        public MarkerSerializer(string marker) => _marker = marker;

        public string Serialize(object? value, Type type, AxiosConfig config) => _marker;

        public object? Deserialize(string text, Type type, AxiosConfig config) => _marker;
    }
}