using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// P0：体积上限（maxContentLength / maxBodyLength）、maxRedirects: 0、
/// 以及「按请求设置实例级选项」的 fail-fast。
/// </summary>
public sealed class LimitsAndRedirectTests
{
    [Fact]
    public async Task MaxContentLength_DeclaredLengthExceedsLimit_ThrowsBeforeReading()
    {
        var payload = new byte[4096];
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "big", c => c.Limits.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Contains("maxContentLength", ex.Message);
    }

    [Fact]
    public async Task MaxContentLength_WithinLimit_ReturnsBody()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<byte[]>(server.Url + "small", c => c.Limits.MaxContentLength = 1024);

        Assert.Equal(payload, response.Data);
    }

    [Fact]
    public async Task MaxContentLength_AppliesToStreamResponse()
    {
        var payload = new byte[8192];
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<Stream>(server.Url + "big", c => c.Limits.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task MaxBodyLength_ExceedsLimit_Throws()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "echo",
            new { name = new string('x', 4096) },
            c => c.Limits.MaxBodyLength = 512));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("maxBodyLength", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task MaxBodyLength_WithinLimit_SendsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = 4096);

        Assert.Equal("""{"name":"bob"}""", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task MaxRedirectsZero_DoesNotFollowRedirect()
    {
        await using var server = StartRedirectServer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = 0,
            },
        });

        // 3xx 在默认 validateStatus 下视为非法，异常里应带回 302 响应本身
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "redir"));

        Assert.Equal(302, ex.Status);
        Assert.Single(server.Received);
        Assert.DoesNotContain(server.Received, r => r.Path == "/final");
    }

    [Fact]
    public async Task MaxRedirectsDefault_FollowsRedirect()
    {
        await using var server = StartRedirectServer();
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "redir");

        Assert.Equal(200, response.Status);
        Assert.Contains(server.Received, r => r.Path == "/final");
    }

    [Fact]
    public async Task PerRequestMaxRedirects_ThrowsBadOptionValue()
    {
        await using var server = StartRedirectServer();
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "redir", c => c.Connection.MaxRedirects = 0));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PerRequestProxy_ThrowsBadOptionValue()
    {
        await using var server = TestJsonServer();
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "x",
            c => c.Connection.Proxy = new System.Net.WebProxy("http://127.0.0.1:9")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    private static TestHttpServer StartRedirectServer()
        => TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                302,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new { ok = true }),
            _ => new TestResponse(404)
        });

    private static TestHttpServer TestJsonServer()
        => TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
}
