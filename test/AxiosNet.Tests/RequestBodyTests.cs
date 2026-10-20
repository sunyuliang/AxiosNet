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
    public async Task Multipart_SendsTextFieldsAndMultipleFileParts()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new
            {
                title = "年报",
                meta = new { author = "bob" },
                files = new[]
                {
                    new FormFile("a.txt", Encoding.UTF8.GetBytes("alpha"), "text/plain"),
                    FormFile.FromBytes("b.txt", Encoding.UTF8.GetBytes("beta"))
                }
            },
            c => c.ContentType = "multipart/form-data");

        var sent = server.Received[0];
        var contentType = ContentType(sent)!;
        Assert.StartsWith("multipart/form-data", contentType);
        Assert.Contains("boundary=", contentType);

        var body = Body(sent);
        Assert.Contains("name=title", body);
        Assert.Contains("年报", body);
        Assert.Contains("meta[author]", body);
        Assert.Contains("bob", body);

        Assert.Equal(2, CountOccurrences(body, "filename="));
        Assert.Contains("a.txt", body);
        Assert.Contains("alpha", body);
        Assert.Contains("text/plain", body);
        Assert.Contains("b.txt", body);
        Assert.Contains("beta", body);
        Assert.Contains("application/octet-stream", body);
    }

    [Fact]
    public async Task Multipart_FromPath_StreamsFileContent()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        await File.WriteAllTextAsync(path, "file-on-disk");

        try
        {
            await using var server = TestHttpServer.Start(EchoJson);
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "upload",
                new { file = FormFile.FromPath(path, "report.txt") },
                c => c.ContentType = "multipart/form-data");

            var body = Body(server.Received[0]);
            Assert.Contains("report.txt", body);
            Assert.Contains("file-on-disk", body);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Multipart_AcceptsRawStreamsAndByteArraysAsFileParts()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-data"));

        await client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?>
            {
                ["title"] = "hello",
                ["stream"] = stream,
                ["bytes"] = new byte[] { 9, 8, 7 }
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body(server.Received[0]);
        Assert.Contains("name=title", body);
        Assert.Contains("stream-data", body);
        Assert.Contains("name=stream", body);
        Assert.Contains("name=bytes", body);
    }

    [Fact]
    public async Task Multipart_RootFileCollection_UsesFieldName()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new[]
            {
                new FormFile("x.txt", Encoding.UTF8.GetBytes("x-content")) { FieldName = "files" },
                new FormFile("y.txt", Encoding.UTF8.GetBytes("y-content")) { FieldName = "files" }
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body(server.Received[0]);
        Assert.Equal(2, CountOccurrences(body, "name=files"));
        Assert.Contains("x-content", body);
        Assert.Contains("y-content", body);
    }

    [Fact]
    public async Task Multipart_HonorsExplicitBoundary()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { a = 1 },
            c => c.ContentType = "multipart/form-data; boundary=----axios-net");

        var sent = server.Received[0];
        Assert.Contains("----axios-net", ContentType(sent));
        Assert.Contains("------axios-net", Body(sent));
    }

    [Fact]
    public async Task Multipart_RootScalarWithoutFieldName_Throws()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "upload",
            42,
            c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("字段名", ex.Cause!.Message);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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

    // ------------------------------------------------------------------
    // multipart 的字段名 / 文件名：Content-Disposition 是 quoted-string，
    // 引号与换行原本会让 HttpClient 抛一句看不出所以然的异常，反斜杠与控制字符更糟——
    // 不报错，但服务端解析回来是「带引号的原文」，字段静默对不上。统一在发出前拦下。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    public async Task Multipart_FieldNameWithDispositionSeparator_ThrowsBadOptionValue(string fieldName)
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?> { [fieldName] = "v" },
            c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains(fieldName, ex.Message);
        Assert.NotNull(ex.Cause);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_FieldNamesThatAreLegalInQuotedString_StillWork()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?>
            {
                ["a[b]"] = "1",
                ["c d"] = "2",
                ["a;b"] = "3",
                ["a=b"] = "4",
                ["中文名"] = "5"
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body(server.Received[0]);
        Assert.Contains("name=\"a[b]\"", body);
        Assert.Contains("name=\"c d\"", body);
        Assert.Contains("name=\"a;b\"", body);
        Assert.Contains("name=\"a=b\"", body);
        // 非 ASCII 字段名由 HttpClient 编成 RFC 2047 encoded-word，服务端解析回「中文名」
        Assert.Contains("name=\"=?utf-8?B?5Lit5paH5ZCN?=\"", body);
    }

    [Theory]
    [InlineData("a\"b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("a\nb.txt")]
    [InlineData("a\rb.txt")]
    [InlineData("a\0b.txt")]
    public void FormFile_FileNameWithDispositionSeparator_IsRejectedUpfront(string fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => new FormFile(fileName, new byte[] { 1 }));

        Assert.Equal("fileName", ex.ParamName);
        Assert.Contains(fileName, ex.Message);
    }
}
