using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 重定向语义：307/308 必须原样重放方法与请求体，303 必须降级为 GET，
/// 同源重定向必须保留 Authorization（axios 在这三点上都与浏览器行为一致）。
/// </summary>
public sealed class RedirectSemanticsTests
{
    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirect_Status307Or308_ReplaysMethodAndBody(int statusCode)
    {
        await using var server = TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                statusCode,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Echo(req),
            _ => new TestResponse(404)
        });
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "redir", new { name = "bob" });

        Assert.Equal(2, server.Received.Count);
        var final = server.Received[1];
        Assert.Equal("/final", final.Path);
        Assert.Equal("POST", final.Method);
        Assert.Equal("""{"name":"bob"}""", Encoding.UTF8.GetString(final.Body));
    }

    [Fact]
    public async Task Redirect_Status303_ConvertsToGet_AndDropsBody()
    {
        await using var server = TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                303,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new { ok = true }),
            _ => new TestResponse(404)
        });
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "redir", new { name = "bob" });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        var final = server.Received[1];
        Assert.Equal("/final", final.Path);
        Assert.Equal("GET", final.Method);
        Assert.Empty(final.Body);
    }

    [Fact]
    public async Task Redirect_InitialRequest_CarriesAuthorizationHeader()
    {
        // 注意：底层 HttpClient 在跟随重定向时会剥离 Authorization（BCL 既定行为，与源无关），
        // 因此这里只锁「库把 Auth 正确应用到原始请求」这一属于自己的契约。
        await using var server = TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                302,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new { ok = true }),
            _ => new TestResponse(404)
        });
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "redir", c => c.Connection.Auth = new AxiosAuth("user", "secret"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(TestJson.BasicAuth("user", "secret"), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Redirect_LocationHeaderValue_ReachesCallerWhenNotFollowed()
    {
        await using var server = TestHttpServer.Start(req => req.Path switch
        {
            "/redir" => new TestResponse(
                302,
                Headers: new Dictionary<string, string> { ["Location"] = "/final" },
                Body: Array.Empty<byte>()),
            "/final" => TestJson.Response(new { ok = true }),
            _ => new TestResponse(404)
        });
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = 0,
            },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "redir"));

        // 3xx 异常的 Response 上应能读到 Location，便于调用方自行处理重定向
        Assert.NotNull(ex.Response);
        Assert.Equal("/final", ex.Response!.GetHeader("Location"));
        Assert.Equal(302, ex.Response.Status);
    }
}
