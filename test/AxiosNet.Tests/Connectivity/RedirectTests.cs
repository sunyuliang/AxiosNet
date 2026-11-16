
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 重定向：默认跟随、`MaxRedirects` 与池选项一致性、307/303 语义、Location 交付。
/// </summary>
public sealed class RedirectTests
{
    /// <summary>
    /// 重定向次数是实例级选项（<see cref="AxiosClientOptions.MaxRedirects"/>）：重定向在连接层由 Handler 执行，
    /// 建池时就定型，单次请求上写不进去。这里用反射守住 API 面，防止它悄悄回到配置上。
    /// </summary>
    [Fact]
    public void MaxRedirects_IsAnInstanceLevelOption()
    {
        Assert.Null(typeof(AxiosConfig.TransportSettings).GetProperty("MaxRedirects"));
        Assert.NotNull(typeof(AxiosClientOptions).GetProperty("MaxRedirects"));
        Assert.True(typeof(AxiosClientOptions).GetProperty("MaxRedirects")!.CanWrite);
    }

    [Fact]
    public async Task OptionsMaxRedirectsMutatedAfterConstruction_Throws_AndPoolIsUntouched()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 构造后改 options：报错，不重建池
        Assert.Throws<AxiosException>(() => client.Options.MaxRedirects = 3);

        // 「没生效」必须落在池上可证伪：池仍是默认的 5
        Assert.Equal(5, client.Pool!.EffectiveOptions.MaxAutomaticRedirections);

        var response = await client.GetAsync(server.Url + "items");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
        Assert.Equal(5, client.Pool!.EffectiveOptions.MaxAutomaticRedirections);
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

    // poolOptions 的重定向上限与显式配置的优先级

    [Fact]
    public void PoolOptionsMaxRedirections_IsNotOverriddenByImplicitDefault()
    {
        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.Equal(10, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void ExplicitMaxRedirects_OverridesPoolOptions()
    {
        using var client = new AxiosClient(
            options: new AxiosClientOptions { MaxRedirects = 12 },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.Equal(12, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void MaxRedirectsZero_DisablesRedirect_ButKeepsPoolLimit()
    {
        using var client = new AxiosClient(
            options: new AxiosClientOptions { MaxRedirects = 0 },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.False(client.Pool!.Options.AllowAutoRedirect);
        Assert.Equal(10, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void NegativeMaxRedirects_ThrowsBadOptionValue()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new AxiosClient(options: new AxiosClientOptions { MaxRedirects = -1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task RedirectLoop_ReportsTooManyRedirects_WithDedicatedCode()
    {
        await using var server = TestHttpServer.Start(req => new TestResponse(
            302,
            Headers: new Dictionary<string, string> { ["Location"] = "/loop" },
            Body: Array.Empty<byte>()));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "loop"));

        Assert.Equal(AxiosException.ErrTooManyRedirects, ex.Code);
        Assert.Equal(AxiosErrorCode.TooManyRedirects, ex.ErrorCode);
        Assert.Equal(302, ex.Status);
        // 初始请求 + 默认上限 5 次跟随后停下。
        Assert.Equal(6, server.Received.Count);
        Assert.Contains("MaxRedirects", ex.Message);
    }

    [Fact]
    public async Task RedirectLimitMessages_UseEffectivePoolValue_NotTheDeclaredDefault()
    {
        await using var server = TestHttpServer.Start(req => new TestResponse(
            302,
            Headers: new Dictionary<string, string> { ["Location"] = "/loop" },
            Body: Array.Empty<byte>()));

        // 共享池把上限压到 2：文案必须说 2（生效值），而不是 options 上那个没写过的默认 5 ——
        // 否则调用方会照着文案去改一个根本没生效的项。
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { MaxAutomaticRedirections = 2 });
        using var client = new AxiosClient(pool: pool);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "loop"));

        Assert.Equal(AxiosException.ErrTooManyRedirects, ex.Code);
        Assert.Contains("MaxRedirects = 2", ex.Message);
    }

    [Fact]
    public async Task Redirect_DisabledByMaxRedirectsZero_HasItsOwnMessage_DistinctFromTooMany()
    {
        await using var server = StartRedirectServer();
        using var client = new AxiosClient(options: new AxiosClientOptions { MaxRedirects = 0 });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "redir"));

        // 关闭跟随不是「次数用尽」：码仍是 ERR_BAD_REQUEST，文案必须点明 MaxRedirects = 0。
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(302, ex.Status);
        Assert.Contains("MaxRedirects = 0", ex.Message);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task MaxRedirectsZero_DoesNotFollowRedirect()
    {
        await using var server = StartRedirectServer();
        using var client = new AxiosClient(options: new AxiosClientOptions { MaxRedirects = 0 });

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

        await client.GetAsync(server.Url + "redir", c => c.Transport.Auth = AxiosCredential.Basic("user", "secret"));

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
        using var client = new AxiosClient(options: new AxiosClientOptions { MaxRedirects = 0 });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "redir"));

        // 3xx 异常的 Response 上应能读到 Location，便于调用方自行处理重定向
        Assert.NotNull(ex.Response);
        Assert.Equal("/final", ex.Response!.GetHeader("Location"));
        Assert.Equal(302, ex.Response.Status);
    }

    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    // ===============================================================
    // 测试夹具
    // ===============================================================


    /// <summary>
    /// 分块写出、块间带 60ms 间隔的正文：让「传满声明长度即锁死」的判定在重发轮次里
    /// 必然产生一次非 force 的上报（而不是靠最终 Complete 的强制上报掩盖问题）。
    /// </summary>
    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly int _chunks;
        private readonly int _chunkSize;

        public SlowChunkedContent(int chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            Headers.ContentLength = (long)chunks * chunkSize;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[_chunkSize];
            for (var i = 0; i < _chunks; i++)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)_chunks * _chunkSize;
            return true;
        }
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

}
