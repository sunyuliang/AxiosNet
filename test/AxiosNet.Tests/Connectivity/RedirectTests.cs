
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
    [Fact]
    public async Task RequestAsync_PerRequestMaxRedirects_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestMaxRedirects_SameAsInstance_IsAllowed()
    {
        // 写与实例相同的值等价于不写。
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = "x",
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });

        Assert.Equal(200, response.Status);
    }

    // ===============================================================
    // Bug 1：MaxRedirects 校验短路用属性默认值替代池生效值
    // ===============================================================

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToDefaultsButDiffersFromPool_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // defaults 上没写 MaxRedirects（属性值仍是默认 5），池实际策略是 3
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        // 按请求显式写 5 且与默认值相同：仍必须生效（服务端只会重定向 3 次）。
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Connection.MaxRedirects = 5));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("MaxRedirects", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_WhenPoolDisablesRedirect_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // AllowAutoRedirect = false 时生效值是 0：按请求写 5 应被静默放行。
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { AllowAutoRedirect = false });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Connection.MaxRedirects = 5));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_Unset_DoesNotThrowEvenWhenPoolDiffersFromDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // 未显式写过的请求携带的是 Defaults 的属性值 5：不能因为池配了 3 就误抛
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync("x");
        Assert.Equal(200, response.Status);
    }

    // ===============================================================
    // 1. 构造后改 Defaults.MaxRedirects：文档承诺「不生效、不报错」，
    //    实际每个请求都在 ValidateMaxRedirects 抛 ERR_BAD_OPTION_VALUE。
    //    根因：MergeConfig / Shortcut 把 Defaults 的 MaxRedirectsSet 一起搬进了生效配置，
    //    校验层无法区分「本次按请求写的」与「从实例默认继承来的」。
    // ===============================================================

    [Fact]
    public async Task DefaultsMaxRedirectsChangedAfterConstruction_DoesNotFailRequests()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // AxiosClient.Defaults 的 XML 文档：连接级选项构造后修改不生效，但不会有任何报错。
        client.Defaults.Connection.MaxRedirects = 3;

        var response = await client.GetAsync(server.Url + "items");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task DefaultsMaxRedirectsViaRequestAsync_DoesNotFailRequestsEither()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 构造之后再改：与快捷方法入口是同一个根因（*Set 标记随 Clone 泄漏进生效配置）
        client.Defaults.Connection.MaxRedirects = 2;

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "items",
            Method = HttpMethod.Get
        });

        Assert.Equal(200, response.Status);
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

    // ------------------------------------------------------------------
    // 实例级选项：按请求写「相同的值」放行，写「不同的值」才报错
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToPoolOptionsValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // defaults 上没写 MaxRedirects（属性值仍是 5），真正生效的是池上的 3：
        // 判定必须读池的实际策略，否则这里会被误判成「与生效值 5 不同」而报错。
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 3);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_Zero_MatchesDisabledRedirectPool_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // AllowAutoRedirect = false 时生效值就是 0（不跟随），按请求写 0 属于等价写法
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 0);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_DiffersFromEffectiveValue_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = 5,
            },
        });

        // 4 与生效值 5 不同：不会生效，必须响亮报错，而不是静默丢弃
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 4));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
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
            new AxiosConfig
            {
                Connection = new()
                {
                    MaxRedirects = 12,
                },
            },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.Equal(12, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void MaxRedirectsZero_DisablesRedirect_ButKeepsPoolLimit()
    {
        using var client = new AxiosClient(
            new AxiosConfig
            {
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.False(client.Pool!.Options.AllowAutoRedirect);
        Assert.Equal(10, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void NegativeMaxRedirects_ThrowsBadOptionValue()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = -1,
            },
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // 按请求写「与生效值相同」的实例级配置放行

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToEffectiveValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = 5,
            },
        });

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 5);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
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