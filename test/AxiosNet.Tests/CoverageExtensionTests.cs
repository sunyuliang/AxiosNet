using System.Net;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 全量代码审查后补充的场景覆盖：HTTP 版本、取消短路、Cookie 开关、上限边界值、
/// multipart/form 的 JSON 树输入、拦截器网络错误恢复、Dispose 幂等、空正文流式响应。
/// 每条都对应一处此前没有用例把守的公共契约。
/// </summary>
public sealed class CoverageExtensionTests
{
    // ---------------------------------------------------------------
    // HttpVersion：请求行版本由 config 决定
    // ---------------------------------------------------------------

    [Fact]
    public async Task HttpVersion_DefaultsTo11()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url);

        Assert.Equal("HTTP/1.1", server.Received[0].Version);
    }

    [Fact]
    public async Task HttpVersion_CanBeDowngradedTo10()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.Connection.HttpVersion = new Version(1, 0));

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.0", server.Received[0].Version);
    }

    // ---------------------------------------------------------------
    // 取消：已取消的令牌必须在发请求前短路，且不与超时混淆
    // ---------------------------------------------------------------

    [Fact]
    public async Task AlreadyCanceledToken_ShortCircuitsWithoutSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, _ => { }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task AlreadyCanceledToken_WithTimeoutSet_IsCanceledNotTimeout()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromSeconds(5), cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    // ---------------------------------------------------------------
    // Cookie：默认关闭；WithCredentials 打开并带上默认系统凭据
    // ---------------------------------------------------------------

    [Fact]
    public async Task UseCookies_DisabledByDefault_DoesNotSendStoredCookies()
    {
        await using var authServer = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/" }));
        await using var apiServer = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        await client.GetAsync(authServer.Url + "login");
        await client.GetAsync(apiServer.Url + "data");

        Assert.False(apiServer.Received[0].Headers.ContainsKey("cookie"));
    }

    [Fact]
    public void WithCredentials_EnablesCookiesAndDefaultCredentialsOnPool()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        Assert.True(client.Pool!.Options.UseCookies);
        Assert.NotNull(client.Pool.Options.Credentials);
    }

    [Fact]
    public void PoolOptions_Clone_CopiesUseCookies()
    {
        var options = new ConnectionPoolOptions { UseCookies = true };

        Assert.True(options.Clone().UseCookies);
    }

    // ---------------------------------------------------------------
    // 上限边界值：恰好等于上限必须放行，超一字节必须拒绝
    // ---------------------------------------------------------------

    [Fact]
    public async Task MaxBodyLength_ExactlyAtLimit_SendsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        const string body = """{"name":"bob"}""";
        var limit = Encoding.UTF8.GetByteCount(body);

        await client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = limit);

        Assert.Equal(body, Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task MaxBodyLength_OneByteOverLimit_ThrowsBeforeSending()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        const string body = """{"name":"bob"}""";
        var limit = Encoding.UTF8.GetByteCount(body) - 1;

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = limit));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task MaxContentLength_ExactlyAtLimit_ReturnsBody()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<byte[]>(server.Url + "exact", c => c.Limits.MaxContentLength = payload.Length);

        Assert.Equal(payload, response.Data);
    }

    [Fact]
    public async Task MaxContentLength_OneByteOverLimit_ThrowsBeforeReading()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<byte[]>(server.Url + "over", c => c.Limits.MaxContentLength = payload.Length - 1));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Single(server.Received);
    }

    // ---------------------------------------------------------------
    // multipart / form：JSON 树（JsonElement）作为数据源
    // ---------------------------------------------------------------

    [Fact]
    public async Task Multipart_RootDictionaryWithJsonElementValues_SendsFields()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var title = JsonDocument.Parse("\"年报\"");
        using var count = JsonDocument.Parse("42");
        var data = new Dictionary<string, object?>
        {
            ["title"] = title.RootElement,
            ["count"] = count.RootElement
        };

        await client.PostAsync(server.Url + "upload", data, c => c.ContentType = "multipart/form-data");

        var body = Encoding.UTF8.GetString(server.Received[0].Body);
        Assert.Contains("name=title", body);
        Assert.Contains("年报", body);
        Assert.Contains("name=count", body);
        Assert.Contains("42", body);
    }

    [Fact]
    public async Task Multipart_JsonElementRoot_FlattensNestedObjectsAndArrays()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var document = JsonDocument.Parse("""{"a":{"b":"c"},"list":[1,2],"gone":null}""");

        await client.PostAsync(server.Url + "upload", document.RootElement, c => c.ContentType = "multipart/form-data");

        var body = Encoding.UTF8.GetString(server.Received[0].Body);
        // 部件名含 [] 时 BCL 会加引号，纯 token 名不加
        Assert.Contains("name=\"a[b]\"", body);
        Assert.Contains("c", body);
        Assert.Contains("name=list", body);
        Assert.Contains("1", body);
        Assert.Contains("2", body);
        Assert.DoesNotContain("gone", body);
    }

    [Fact]
    public async Task FormUrlEncoded_RootArray_UsesIndexKeys()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", new[] { 1, 2, 3 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("[0]=1&[1]=2&[2]=3", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task FormUrlEncoded_JsonElementRoot_FlattensLikeObject()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var document = JsonDocument.Parse("""{"a":1,"b":["x","y"]}""");

        await client.PostAsync(server.Url + "form", document.RootElement,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("a=1&b[]=x&b[]=y", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task FormUrlEncoded_RootScalar_IsSkippedInsteadOfInventingAKey()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", 42,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Empty(server.Received[0].Body);
    }

    // ---------------------------------------------------------------
    // 拦截器：OnRejected 可以从网络层错误恢复（不只是错误状态码）
    // ---------------------------------------------------------------

    [Fact]
    public async Task ResponseInterceptor_RejectedCanRecoverFromNetworkError()
    {
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(
            onFulfilled: (AxiosResponse response) => response,
            onRejected: (Exception ex) =>
            {
                var error = Assert.IsType<AxiosException>(ex);
                Assert.Equal(AxiosException.ErrNetwork, error.Code);
                Assert.Null(error.Response);
                return new AxiosResponse
                {
                    Status = 200,
                    StatusText = "Recovered",
                    Data = "fallback"
                };
            });

        var response = await client.GetAsync(
            "http://127.0.0.1:1/unreachable",
            c => c.ResponseType = ResponseType.Text);

        Assert.Equal(200, response.Status);
        Assert.Equal("fallback", Assert.IsType<string>(response.Data));
    }

    // ---------------------------------------------------------------
    // Dispose / DisposeAsync 幂等
    // ---------------------------------------------------------------

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var client = new AxiosClient();

        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var client = new AxiosClient();

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RequestAfterDisposeAsync_ThrowsCanceled()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var client = new AxiosClient();
        await client.DisposeAsync();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    // ---------------------------------------------------------------
    // 空正文 / HEAD 的流式响应
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ZeroLengthBody_CompletesImmediately()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Array.Empty<byte>(),
            ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url);
        await using var stream = response.Data!;

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(0, buffer.Length);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task HeadRequest_WithStreamResponseType_ReturnsEmptyStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: TestJson.Bytes(new { ok = true }),
            ContentType: "application/json"));
        using var client = new AxiosClient();

        var response = await client.HeadAsync<Stream>(server.Url);
        await using var stream = response.Data!;

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(0, buffer.Length);
        Assert.Equal("HEAD", server.Received[0].Method);
    }
}
