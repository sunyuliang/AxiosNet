using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// params 序列化（数组/嵌套/已有 query）、data 转换（JSON/表单/字符串/字节）、
/// 默认头、Basic Auth 等配置能力。
/// </summary>
public sealed class ParamsAndContentTests
{
    [Fact]
    public async Task Params_Object_ArraysUseBrackets_NestedUseBracketNotation_NullSkipped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
            c.Params = new
            {
                q = "a b",
                ids = new[] { 1, 2 },
                nested = new { page = 1 },
                missing = (string?)null
            });

        var query = server.Received[0].Query;
        Assert.Equal("q=a%20b&ids[]=1&ids[]=2&nested[page]=1", query);
    }

    [Fact]
    public async Task Params_Dictionary_IsSerializedTheSameWay()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
            c.Params = new Dictionary<string, object?>
            {
                ["tags"] = new[] { "x", "y" },
                ["filter"] = new Dictionary<string, object?> { ["kind"] = "book" }
            });

        var query = server.Received[0].Query;
        Assert.Equal("tags[]=x&tags[]=y&filter[kind]=book", query);
    }

    [Fact]
    public async Task Params_AppendedToUrlThatAlreadyHasQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search?x=1", c => c.Params = new { y = 2 });

        Assert.Equal("x=1&y=2", server.Received[0].Query);
    }

    [Fact]
    public async Task Post_Object_SendsJsonAndReceivesJsonRoundTrip()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var person = new Person("bob", 30);
        var response = await client.PostAsync<Person>(server.Url + "echo", person);

        Assert.Equal(person, response.Data);
        var sent = server.Received[0];
        Assert.StartsWith("application/json", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("\"name\":\"bob\"", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_Dictionary_WithFormContentType_SendsFormUrlEncoded()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        var form = new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        };

        // 表单编码由 config.ContentType 显式指定，不再由「字典类型」推断
        await client.PostAsync(
            server.Url + "form",
            form,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        var sent = server.Received[0];
        Assert.StartsWith("application/x-www-form-urlencoded", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice&role=admin", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_String_ContentTypeCanBeOverridden()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "raw",
            "raw-text",
            c => c.Headers.Set("Content-Type", "text/plain; charset=utf-8"));

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=utf-8", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("raw-text", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_String_WithRequestEncoding_UsesEncodingAndCharsetHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "text/plain"));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "raw", "你好", c => c.Serialization.RequestEncoding = Encoding.Unicode);

        var sent = server.Received[0];
        Assert.Equal("你好", Encoding.Unicode.GetString(sent.Body));
        Assert.Contains("charset=utf-16", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_Dictionary_WithRequestEncoding_PercentEncodesWithThatEncoding()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["name"] = "é" },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.RequestEncoding = Encoding.Latin1;
            });

        var sent = server.Received[0];
        // Latin1 下 'é' 为单字节 0xE9 -> %E9（默认 UTF-8 会是 %C3%A9）
        Assert.Equal("name=%E9", Encoding.ASCII.GetString(sent.Body));
        Assert.Contains("charset=iso-8859-1", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_Dictionary_WithNonUtf8RequestEncoding_WritesAsciiEscapedBytes()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["k"] = "é" },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.RequestEncoding = Encoding.Unicode;
            });

        var sent = server.Received[0];
        // UTF-16LE 下 'k' -> 6B 00、'é' -> E9 00，故转义为 "k%00=%E9%00"（服务端按 charset 反转义可还原）。
        // 关键点：转义结果恒为 ASCII，wire 上应是「每字符 1 字节」；
        // 不能被再按 UTF-16 编成双字节流（22 字节），那会破坏 form-urlencoded 格式。
        Assert.Equal("k%00=%E9%00", Encoding.ASCII.GetString(sent.Body));
        Assert.Equal(11, sent.Body.Length);
        Assert.Contains("charset=utf-16", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_MultipartFormDataContent_PassesThroughUntouched()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: req.Headers.GetValueOrDefault("content-type") ?? "application/json"));
        using var client = new AxiosClient();

        using var multipart = new MultipartFormDataContent("axios-net-boundary");
        multipart.Add(new StringContent("alice"), "name");
        multipart.Add(new ByteArrayContent(new byte[] { 9, 8, 7 }), "file", "data.bin");

        await client.PostAsync(server.Url + "upload", multipart, c => c.ResponseType = ResponseType.Text);

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        var rawMultipart = Encoding.UTF8.GetString(sent.Body);
        Assert.Contains("form-data", rawMultipart);
        Assert.Contains("name=name", rawMultipart);
        Assert.Contains("alice", rawMultipart);
        Assert.Contains("data.bin", rawMultipart);
    }

    [Fact]
    public async Task Auth_ConfigSendsBasicAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "secure", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        Assert.Equal(TestJson.BasicAuth("user", "pass"), server.Received[0].Headers.GetValueOrDefault("authorization"));
    }

    [Fact]
    public async Task DefaultHeaders_AreSent_AndPerRequestHeadersOverrideThem()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new
        {
            def = req.Headers.GetValueOrDefault("x-default"),
            version = req.Headers.GetValueOrDefault("x-version"),
            perRequest = req.Headers.GetValueOrDefault("x-request")
        }));

        var defaults = new AxiosConfig();
        defaults.Headers.Set("X-Default", "yes");
        defaults.Headers.Set("X-Version", "v1");

        using var client = new AxiosClient(defaults);

        var response = await client.GetAsync<JsonElement>(server.Url, c =>
        {
            c.Headers.Set("X-Version", "v2");
            c.Headers.Set("X-Request", "1");
        });

        Assert.Equal("yes", response.Data.GetProperty("def").GetString());
        Assert.Equal("v2", response.Data.GetProperty("version").GetString());
        Assert.Equal("1", response.Data.GetProperty("perRequest").GetString());
    }

    [Fact]
    public async Task ResponseHeaders_AreExposedCaseInsensitively()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Headers: new Dictionary<string, string> { ["X-Multi"] = "one" }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        Assert.Equal("one", response.GetHeader("x-multi"));
        Assert.True(response.Headers.ContainsKey("X-MULTI"));
    }
}
