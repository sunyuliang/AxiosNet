using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// P1：paramsSerializer / formSerializer（字段名规则）/ transformRequest / transformResponse /
/// postForm·putForm·patchForm。
/// </summary>
public sealed class SerializationAndTransformTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    [Fact]
    public async Task ParamsSerializer_Custom_ReplacesBuiltInEncoding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
        {
            c.Params = new Dictionary<string, object?> { ["ids"] = new[] { 1, 2, 3 }, ["q"] = "x y" };
            c.Serialization.ParamsSerializer = p =>
            {
                var dict = (Dictionary<string, object?>)p!;
                return $"ids={string.Join(",", (int[])dict["ids"]!)}&q={Uri.EscapeDataString((string)dict["q"]!)}";
            };
        });

        Assert.Equal("ids=1,2,3&q=x%20y", server.Received[0].Query);
    }

    [Fact]
    public async Task FormFieldNameSelector_AppliesToFormUrlEncoded()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new { name = "alice", filter = new { kind = "book" } },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.FormFieldNameSelector = (prefix, key) => prefix is null ? key : $"{prefix}.{key}";
            });

        Assert.Equal("name=alice&filter.kind=book", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormFieldNameSelector_AppliesToMultipart()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { name = "alice", meta = new { author = "bob" } },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Serialization.FormFieldNameSelector = (prefix, key) => prefix is null ? key : $"{prefix}_{key}";
            });

        var body = Body(server.Received[0]);
        Assert.Contains("name=meta_author", body);
        Assert.Contains("bob", body);
    }

    [Fact]
    public async Task TransformRequest_RunsBeforeEncoding()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "echo", new { secret = "raw" }, c =>
        {
            c.TransformRequest.Add((data, _) => new { masked = "***" });
            c.TransformRequest.Add((data, _) =>
            {
                // 第二个 transform 拿到的是上一个 transform 的返回值
                Assert.NotNull(data);
                return data;
            });
        });

        Assert.Equal("""{"masked":"***"}""", Body(server.Received[0]));
    }

    [Fact]
    public async Task TransformResponse_ProcessesParsedData()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { code = 0, data = new { name = "alice" } }));
        using var client = new AxiosClient();

        var response = await client.GetAsync<System.Text.Json.JsonElement>(server.Url + "wrap", c =>
            c.TransformResponse.Add((data, _) =>
                ((System.Text.Json.JsonElement)data!).GetProperty("data")));

        Assert.Equal("alice", response.Data.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PostForm_DefaultsToMultipart()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("name=name", Body(sent));
        Assert.Contains("alice", Body(sent));
    }

    [Fact]
    public async Task PostForm_RespectsExplicitContentType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostFormAsync(
            server.Url + "form",
            new { name = "alice" },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("application/x-www-form-urlencoded", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice", Body(server.Received[0]));
    }

    [Fact]
    public async Task PostForm_IgnoresNonFormInstanceContentType()
    {
        // 实例默认值为 JSON 接口设置过 ContentType：它是「默认值」，不是本次调用的意见，
        // 不能把 PostFormAsync 的 multipart 承诺压掉（否则字段被按 JSON 发出去，且没有任何提示）。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosContentType.Json;

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("name=name", Body(sent));
        Assert.Contains("alice", Body(sent));
    }

    [Fact]
    public async Task PostForm_KeepsInstanceFormUrlEncodedDefault()
    {
        // 实例默认值本身就是表单编码＝「这个客户端专发表单」的选择，方法默认值不抢它。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosConfig.FormUrlEncodedContentType;

        await client.PostFormAsync(server.Url + "form", new { name = "alice" });

        Assert.Equal("application/x-www-form-urlencoded", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice", Body(server.Received[0]));
    }

    [Fact]
    public async Task PostForm_KeepsInstanceMultipartDefaultWithBoundary()
    {
        // 实例默认值里的 multipart 参数（固定 boundary）同样保留：覆盖会把调用方指定的边界换掉。
        const string boundary = "----axios-net-form";
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosContentType.MultipartFormData + "; boundary=" + boundary;

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        var contentType = sent.Headers.GetValueOrDefault("content-type")!;
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        Assert.Contains(boundary, contentType);
        Assert.Contains("--" + boundary, Body(sent));
    }

    [Fact]
    public async Task PostForm_ConfigureStillBeatsInstanceContentType()
    {
        // configure 是本次调用的显式意见，优先级高于方法默认值与实例默认值。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosConfig.FormUrlEncodedContentType;

        await client.PostFormAsync(
            server.Url + "json",
            new { name = "alice" },
            c => c.ContentType = AxiosContentType.Json);

        Assert.Equal("application/json", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("{\"name\":\"alice\"}", Body(server.Received[0]));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task PutAndPatchForm_UseMultipartAndExpectedMethod(string method)
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        if (method == "PUT")
        {
            await client.PutFormAsync(server.Url + "upload", new { name = "alice" });
        }
        else
        {
            await client.PatchFormAsync(server.Url + "upload", new { name = "alice" });
        }

        Assert.Equal(method, server.Received[0].Method);
        Assert.StartsWith("multipart/form-data", server.Received[0].Headers.GetValueOrDefault("content-type"));
    }
}
