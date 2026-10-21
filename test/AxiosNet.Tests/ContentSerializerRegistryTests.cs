using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 自动模式：<b>未注入</b>序列化器时（<see cref="AxiosContent.Serializer"/> 为 <c>null</c>）
/// 按 <c>Content-Type</c> 查注册表；未注册则回落内置 JSON。
/// <para>
/// 为避免与并行执行的其它测试互相干扰，除「XML 端到端」一条外，注册键都使用本文件私有的
/// 媒体类型（<c>application/x-test-*</c>），并在 <c>finally</c> 里注销。
/// </para>
/// </summary>
public sealed class ContentSerializerRegistryTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    [Fact]
    public async Task AutoMode_UsesRegisteredSerializer_ForRequestAndResponse()
    {
        AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
        try
        {
            await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
                ? new TestResponse(
                    200,
                    Body: Encoding.UTF8.GetBytes("<XmlOrder><Id>7</Id><Name>book</Name></XmlOrder>"),
                    ContentType: "application/xml; charset=utf-8")
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
            using var client = new AxiosClient(new AxiosConfig { ContentSerializer = new MarkerSerializer("injected") });

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

    [Fact]
    public void Register_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AxiosContent.Register("application/x-test-null", null!));
        Assert.Throws<ArgumentException>(() => AxiosContent.Register("   ", new MarkerSerializer("x")));
        Assert.Throws<ArgumentException>(() => AxiosContent.Register("; charset=utf-8", new MarkerSerializer("x")));
        Assert.False(AxiosContent.Unregister("application/x-never-registered"));
    }

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
