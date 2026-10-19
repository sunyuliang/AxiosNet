using System.Dynamic;
using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 请求体编码契约：
/// - 默认 application/json，且格式由 <see cref="AxiosConfig.ContentType"/> 决定，与 data 的运行时类型无关；
/// - 表单编码需显式设置 ContentType；
/// - 所有快捷方法（含 GET/DELETE/HEAD/OPTIONS）都能携带任意类型的 data。
/// </summary>
public sealed class RequestBodyTests
{
    private static TestResponse EchoJson(TestRequest req) => TestJson.Echo(req);

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string? ContentType(TestRequest request) => request.Headers.GetValueOrDefault("content-type");

    [Fact]
    public async Task DefaultContentType_IsJson_ForEveryStructuredShape()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        object dynamicPayload = new ExpandoObject();
        ((IDictionary<string, object?>)dynamicPayload)["name"] = "bob";

        await client.PostAsync(server.Url + "object", new { name = "bob" });
        await client.PostAsync(server.Url + "dictionary", new Dictionary<string, object?> { ["name"] = "bob" });
        await client.PostAsync(server.Url + "dynamic", dynamicPayload);
        await client.PostAsync(server.Url + "list", new[] { new { name = "bob" } });

        Assert.All(server.Received, request => Assert.Equal("application/json", ContentType(request)));
        Assert.Equal("""{"name":"bob"}""", Body(server.Received[0]));
        Assert.Equal("""{"name":"bob"}""", Body(server.Received[1]));
        Assert.Equal("""{"name":"bob"}""", Body(server.Received[2]));
        Assert.Equal("""[{"name":"bob"}]""", Body(server.Received[3]));
    }

    [Fact]
    public async Task DefaultContentType_IsJson_EvenForDictionary_NoImplicitFormEncoding()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        });

        var sent = server.Received[0];
        Assert.Equal("application/json", ContentType(sent));
        Assert.Equal("""{"name":"alice","role":"admin"}""", Body(sent));
    }

    [Fact]
    public async Task ContentType_InstanceDefault_Applies_AndPerRequestOverrideWins()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            ContentType = AxiosConfig.FormUrlEncodedContentType
        });

        var form = new Dictionary<string, object?> { ["name"] = "alice" };

        // 实例默认：表单
        await client.PostAsync("default", form);
        // 按请求覆盖：JSON
        await client.PostAsync("override", form, c => c.ContentType = AxiosConfig.JsonContentType);

        Assert.Equal("application/x-www-form-urlencoded", ContentType(server.Received[0]));
        Assert.Equal("name=alice", Body(server.Received[0]));

        Assert.Equal("application/json", ContentType(server.Received[1]));
        Assert.Equal("""{"name":"alice"}""", Body(server.Received[1]));
    }

    [Fact]
    public async Task FormContentType_FlattensNestedObjectsAndArrays_AndSkipsNulls()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new
            {
                name = "a b",
                tags = new[] { "x", "y" },
                filter = new { kind = "book" },
                skip = (string?)null
            },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("application/x-www-form-urlencoded", ContentType(server.Received[0]));
        Assert.Equal("name=a+b&tags[]=x&tags[]=y&filter[kind]=book", Body(server.Received[0]));
    }

    [Fact]
    public async Task ContentType_WithExplicitCharset_IsNotRewritten()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["name"] = "é" },
            c =>
            {
                c.ContentType = "application/x-www-form-urlencoded; charset=iso-8859-1";
                c.RequestEncoding = Encoding.Latin1;
            });

        var sent = server.Received[0];
        Assert.Equal("application/x-www-form-urlencoded; charset=iso-8859-1", ContentType(sent));
        Assert.Equal("name=%E9", Encoding.ASCII.GetString(sent.Body));
    }

    [Fact]
    public async Task NonJsonTextContentType_KeepsStructuredBodyAsJsonText()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "xml", new { name = "bob" }, c => c.ContentType = "text/plain");

        var sent = server.Received[0];
        Assert.Equal("text/plain", ContentType(sent));
        Assert.Equal("""{"name":"bob"}""", Body(sent));
    }

    [Fact]
    public async Task BinaryBody_UsesConfiguredContentType_NotTheJsonDefault()
    {
        var payload = new byte[] { 0, 1, 2, 250, 255 };
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "bin", payload, c => c.ContentType = "application/octet-stream");

        var sent = server.Received[0];
        Assert.Equal("application/octet-stream", ContentType(sent));
        Assert.Equal(payload, sent.Body);
    }

    [Fact]
    public async Task MultipartContentType_IsRejected_WithActionableMessage()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.PostAsync(
            server.Url + "upload",
            new { name = "alice" },
            c => c.ContentType = "multipart/form-data"));

        Assert.Contains("MultipartFormDataContent", ex.Message);
    }

    [Fact]
    public async Task HttpContentPassthrough_KeepsItsOwnContentType()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var raw = new StringContent("hello", Encoding.UTF8, "text/plain");
        await client.PostAsync(server.Url + "raw", raw, c => c.ContentType = AxiosConfig.JsonContentType);

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=utf-8", ContentType(sent));
        Assert.Equal("hello", Body(sent));
    }

    [Fact]
    public async Task StringBody_IsNotParsed_AheadOfConfiguredContentType()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "raw", """{"already":"json"}""");

        var sent = server.Received[0];
        Assert.Equal("application/json", ContentType(sent));
        Assert.Equal("""{"already":"json"}""", Body(sent));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("HEAD")]
    public async Task EveryMethod_AcceptsBodyOfAnyShape(string method)
    {
        await using var server = TestHttpServer.Start(req => req.Method == "HEAD"
            ? new TestResponse(204, "No Content")
            : TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        switch (method)
        {
            case "GET":
                await client.GetAsync(server.Url + "x", new { name = "bob" });
                await client.GetAsync<JsonElement>(server.Url + "x", new Dictionary<string, object?> { ["name"] = "bob" });
                break;
            case "DELETE":
                await client.DeleteAsync(server.Url + "x", new { name = "bob" });
                await client.DeleteAsync(server.Url + "x", "raw-text");
                break;
            case "OPTIONS":
                await client.OptionsAsync(server.Url + "x", new { name = "bob" });
                await client.OptionsAsync(server.Url + "x", new byte[] { 1, 2, 3 });
                break;
            case "HEAD":
                await client.HeadAsync(server.Url + "x", new { name = "bob" });
                break;
        }

        Assert.All(server.Received, request => Assert.Equal(method, request.Method));
        Assert.All(server.Received, request => Assert.True(request.Body.Length > 0));

        var first = server.Received[0];
        Assert.Equal("application/json", ContentType(first));
        Assert.Equal("""{"name":"bob"}""", Body(first));
    }

    [Fact]
    public async Task GetAsync_WithStreamBody_IsSupported()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-body"));
        await client.GetAsync(server.Url + "x", stream);

        Assert.Equal("GET", server.Received[0].Method);
        Assert.Equal("stream-body", Body(server.Received[0]));
    }
}
