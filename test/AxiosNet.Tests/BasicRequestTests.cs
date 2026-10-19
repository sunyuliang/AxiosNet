using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// axios 核心能力：请求/响应结构、各 HTTP 方法、各种 responseType、重定向、实例创建、静态门面。
/// </summary>
public sealed class BasicRequestTests
{
    [Fact]
    public async Task GetAsync_TypedJson_ReturnsFullAxiosResponseShape()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(
                Headers: new Dictionary<string, string> { ["X-Greeting"] = "hello" },
                Body: TestJson.Bytes(new Person("alice", 18))));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "users/1");

        Assert.Equal(200, response.Status);
        Assert.Equal("OK", response.StatusText);
        Assert.NotNull(response.Data);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal(18, response.Data.Age);
        Assert.Equal("hello", response.GetHeader("X-Greeting"));
        Assert.NotNull(response.Config);
        Assert.NotNull(response.Request);
        Assert.Equal(HttpMethod.Get, response.Request!.Method);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/1", received.Path);
    }

    [Fact]
    public async Task GetAsync_NonGeneric_JsonDataIsJsonElement()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + "people/1");

        var element = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal("alice", element.GetProperty("name").GetString());
        Assert.Equal(18, element.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task ResponseType_Text_ReturnsString()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("pong"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("pong", Assert.IsType<string>(response.Data));
    }

    [Fact]
    public async Task ResponseType_Stream_ReturnsReadableStream()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("stream-body"), ContentType: "text/plain"));

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Stream);

        var stream = Assert.IsAssignableFrom<Stream>(response.Data);
        using var reader = new StreamReader(stream);
        Assert.Equal("stream-body", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task PostAsync_Bytes_ResponseTypeArrayBuffer_RoundTrips()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/octet-stream"));

        using var client = new AxiosClient();
        var payload = new byte[] { 0, 1, 2, 250, 255 };

        var response = await client.PostAsync<byte[]>(
            server.Url + "bytes",
            payload,
            c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, response.Data);
        Assert.Equal(payload, server.Received[0].Body);
    }

    [Fact]
    public async Task ResponseType_ArrayBuffer_NonGeneric_ReturnsBytes()
    {
        var payload = new byte[] { 1, 2, 3, 254, 255 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, Assert.IsType<byte[]>(response.Data));
    }

    [Fact]
    public async Task GetAsync_OfString_PlainText_ReturnsRawText()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("hello world"), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "ping");

        Assert.Equal("hello world", response.Data);
    }

    [Fact]
    public async Task GetAsync_OfString_JsonString_ReturnsRawText()
    {
        // 原始内容优先：即使是 JSON 字符串也原样返回（含引号），不再自动剥离
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("\"hello\""), ContentType: "application/json; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url);

        Assert.Equal("\"hello\"", response.Data);
    }

    [Fact]
    public async Task GetAsync_OfInt_BareNumberText_IsParsed()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("42"), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<int>(server.Url);

        Assert.Equal(42, response.Data);
    }

    [Fact]
    public async Task GetAsync_OfDateTime_BareDateText_IsParsed()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("2024-01-02"), ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<DateTime>(server.Url);

        Assert.Equal(new DateTime(2024, 1, 2), response.Data);
    }

    [Fact]
    public async Task Response_CharsetInContentType_IsAutoDetected()
    {
        var body = Encoding.Unicode.GetBytes("你好"); // UTF-16LE
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain; charset=utf-16"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task Response_Bom_IsAutoDetected_WhenNoCharset()
    {
        var body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("你好")).ToArray();
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task ResponseEncoding_OverridesAutoDetection()
    {
        // 服务端未声明 charset、字节为 Latin1：默认按 UTF-8 解码会乱码，强制 Latin1 后正确
        var body = Encoding.Latin1.GetBytes("café");
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain"));
        using var client = new AxiosClient();

        var auto = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.NotEqual("café", auto.Data);

        var forced = await client.GetAsync(server.Url, c =>
        {
            c.ResponseType = ResponseType.Text;
            c.ResponseEncoding = Encoding.Latin1;
        });
        Assert.Equal("café", forced.Data);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task ShortcutMethods_SendExpectedHttpMethod(string method)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        switch (method)
        {
            case "PUT":
                await client.PutAsync(server.Url, new { name = "x" });
                break;
            case "PATCH":
                await client.PatchAsync(server.Url, new { name = "x" });
                break;
            case "DELETE":
                await client.DeleteAsync(server.Url);
                break;
            case "OPTIONS":
                await client.OptionsAsync(server.Url);
                break;
        }

        Assert.Equal(method, server.Received[0].Method);
    }

    [Fact]
    public async Task HeadAsync_NoBody()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new AxiosClient();

        var response = await client.HeadAsync(server.Url);

        Assert.Equal(204, response.Status);
        Assert.Equal("No Content", response.StatusText);
        Assert.Null(response.Data);
        Assert.Equal("HEAD", server.Received[0].Method);
        // HEAD 响应不能写 body，连接上不能残留字节
        Assert.Equal(1, server.RequestsPerConnection.Values.Single());
    }

    [Fact]
    public async Task Redirects_AreFollowedByDefault()
    {
        await using var server = TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                302,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new Person("redirected", 1)),
            _ => new TestResponse(404)
        });

        using var client = new AxiosClient();
        var response = await client.GetAsync<Person>(server.Url + "redir");

        Assert.Equal(200, response.Status);
        Assert.Equal("redirected", response.Data!.Name);
        Assert.Contains(server.Received, r => r.Path == "/final");
    }

    [Fact]
    public async Task BaseAddress_IsCombinedWithRelativeUrl()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { path = req.Path }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url + "base/" });

        await client.GetAsync("users");
        await client.GetAsync("/items");
        // 绝对地址优先于 baseURL
        await client.GetAsync(server.Url + "other");

        Assert.Equal("/base/users", server.Received[0].Path);
        Assert.Equal("/base/items", server.Received[1].Path);
        Assert.Equal("/other", server.Received[2].Path);
    }

    [Fact]
    public async Task Create_BuildsIndependentInstanceWithDefaults()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));

        var client = Axios.Create(c =>
        {
            c.BaseAddress = server.Url;
            c.Headers.Set("X-Trace", "instance-default");
        });

        using (client)
        {
            var response = await client.GetAsync<JsonElement>("anything");
            Assert.Equal("instance-default", response.Data.GetProperty("trace").GetString());
            Assert.Equal("/anything", server.Received[0].Path);
        }
    }

    [Fact]
    public async Task StaticAxios_GetAsync_WorksLikeAxiosGet()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("static", 1)));

        var response = await Axios.GetAsync<Person>(server.Url + "static-call");

        Assert.Equal("static", response.Data!.Name);
        Assert.Equal("/static-call", server.Received[0].Path);
    }

    [Fact]
    public async Task RequestAsync_ActionConfigured_GoesThroughSamePipeline()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { echo = req.Headers.GetValueOrDefault("x-custom") }));

        using var client = new AxiosClient();
        var response = await client.RequestAsync<JsonElement>(c =>
        {
            c.Url = server.Url + "explicit";
            c.Method = AxiosMethod.Get;
            c.Headers.Set("X-Custom", "yes");
        });

        Assert.Equal("yes", response.Data.GetProperty("echo").GetString());
    }
}
