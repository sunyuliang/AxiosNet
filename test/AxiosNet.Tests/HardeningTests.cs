using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>加固用例：头部注入、超时收敛、限流、Cookie 与连接池边界。</summary>
public sealed class HardeningTests
{
    // 请求头 CRLF 注入

    [Theory]
    [InlineData("ok\r\nX-Injected: pwned")]
    [InlineData("ok\nX-Injected: pwned")]
    [InlineData("ok\rX-Injected: pwned")]
    [InlineData("ok\0pwned")]
    public void HeaderValue_WithControlChars_IsRejected(string value)
    {
        var headers = new AxiosHeaders();

        var ex = Assert.Throws<ArgumentException>(() => headers.Set("X-Test", value));

        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData("X-A: X-B")]
    [InlineData("X A")]
    [InlineData("X-A\r\n")]
    [InlineData("X\tA")]
    public void HeaderName_OutsideTokenCharset_IsRejected(string name)
    {
        var headers = new AxiosHeaders();

        var ex = Assert.Throws<ArgumentException>(() => headers.Set(name, "v"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void HeaderValue_WithoutControlChars_IsAcceptedVerbatim()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "a=b; c, d");

        Assert.Equal("a=b; c, d", headers.Get("x-trace"));
    }

    [Fact]
    public async Task HeaderValue_ReachesServerUnchanged()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "ping", c => c.Headers.Set("X-Trace", "a=b; c, d"));

        Assert.Equal("a=b; c, d", server.Received[0].Headers["x-trace"]);
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

    // 流式正文截断与非流式同错误模型

    [Fact]
    public async Task TruncatedStreamingBody_IsWrappedAsNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("0123456789"),
            ContentType: "application/octet-stream",
            ContentLengthOverride: 100,
            CloseConnection: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "truncated");
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.IsAssignableFrom<IOException>(ex.Cause);
    }

    /// <summary>验证 Timeout=0 时正文截断同样报 ERR_NETWORK。</summary>
    [Fact]
    public async Task TruncatedStreamingBody_WithoutTimeout_IsStillNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("0123456789"),
            ContentType: "application/octet-stream",
            ContentLengthOverride: 100,
            CloseConnection: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "truncated", c => c.Timeout = TimeSpan.Zero);
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    // 超大 Timeout 收敛

    [Theory]
    [InlineData(60)]
    [InlineData(365)]
    public async Task HugeTimeout_IsClamped_BufferedRequest(int days)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "ping", c => c.Timeout = TimeSpan.FromDays(days));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task HugeTimeout_IsClamped_StreamingResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"ok\":true}")));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "ping", c => c.Timeout = TimeSpan.MaxValue);
        await using (var stream = response.Data!)
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task NegativeTimeout_ThrowsBadOptionValue()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/x", c => c.Timeout = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task NegativeMaxRateAndLimits_ThrowBadOptionValue()
    {
        using var client = new AxiosClient();

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxRate = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxContentLength = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxBodyLength = -1))).Code);
    }

    // params 不能落到 URL fragment 里

    [Theory]
    [InlineData("http://a.com/p#top", "http://a.com/p?q=1#top")]
    [InlineData("http://a.com/p?z=0#top", "http://a.com/p?z=0&q=1#top")]
    public void Params_AreAppendedBeforeFragment(string url, string expected)
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.Url = url;
            c.Params = new { q = 1 };
        });

        Assert.Equal(expected, uri);
    }

    [Fact]
    public async Task Params_WithFragment_ReachTheServer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "p#top", c => c.Params = new { q = 1 });

        Assert.Equal("/p", server.Received[0].Path);
        Assert.Equal("q=1", server.Received[0].Query);
    }

    // PostAsync(url, null) 落到 AxiosConfig 重载

    [Fact]
    public async Task PostAsync_WithNullConfig_FallsBackToEmptyBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "x", (AxiosConfig)null!);

        Assert.Equal(200, response.Status);
        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task ConfigOverloads_ExistForPutPatchHeadOptions()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new AxiosClient();

        var put = await client.PutAsync(server.Url + "x", new AxiosConfig { Data = new { n = 1 } });
        var patch = await client.PatchAsync(server.Url + "x", new AxiosConfig { Data = new { n = 2 } });
        var options = await client.OptionsAsync(server.Url + "x", new AxiosConfig());
        var head = await client.HeadAsync(server.Url + "x", new AxiosConfig());

        Assert.Equal(204, put.Status);
        Assert.Equal(204, patch.Status);
        Assert.Equal(204, options.Status);
        Assert.Equal(204, head.Status);
        Assert.Equal(new[] { "PUT", "PATCH", "OPTIONS", "HEAD" }, server.Received.Select(r => r.Method));
    }

    [Fact]
    public void Clone_CopiesMutableDictionaryContainers()
    {
        var sentinel = new object();
        var defaults = new AxiosConfig
        {
            Params = new Dictionary<string, object?> { ["p"] = sentinel },
            Data = new Dictionary<string, object?> { ["d"] = 1 }
        };

        var clone = defaults.Clone();

        ((Dictionary<string, object?>)clone.Params!)["added"] = 2;
        ((Dictionary<string, object?>)clone.Data!)["added"] = 2;

        Assert.Single((Dictionary<string, object?>)defaults.Params!);
        Assert.Single((Dictionary<string, object?>)defaults.Data!);

        // 元素仍按引用共享。
        Assert.Same(sentinel, ((Dictionary<string, object?>)clone.Params!)["p"]);
    }

    [Fact]
    public void Clone_PreservesDictionaryFamilyAndComparer()
    {
        var defaults = new AxiosConfig
        {
            Params = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Api_Key"] = 1 },
            Data = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 }
        };

        var clone = defaults.Clone();

        var parameters = Assert.IsType<Dictionary<string, object?>>(clone.Params);
        Assert.True(parameters.ContainsKey("api_key"));

        var body = Assert.IsType<SortedDictionary<string, object?>>(clone.Data);
        Assert.True(body.ContainsKey("a"));

        parameters["added"] = 2;
        Assert.Single((Dictionary<string, object?>)defaults.Params!);
    }

    [Fact]
    public void Clone_PreservesConcurrentDictionary()
    {
        var source = new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        source["Key"] = 1;

        var clone = new AxiosConfig { Params = source }.Clone();

        var parameters = Assert.IsType<ConcurrentDictionary<string, object?>>(clone.Params);
        Assert.NotSame(source, parameters);
        Assert.True(parameters.ContainsKey("key"));
    }

    [Fact]
    public async Task NullParamsAndData_ClearInheritedDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Data = new { inherited = true },
            Params = new { inherited = 1 }
        });

        // 按请求显式写 null 才会摘掉实例默认值。
        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "x",
            Method = HttpMethod.Post,
            Data = null,
            Params = null
        });

        Assert.Equal(200, response.Status);
        var received = Assert.Single(server.Received);
        Assert.Equal("/x", received.Path);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task PostAsync_WithNullBody_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "ping", (object?)null);

        Assert.Equal(200, response.Status);
    }

    // 流式响应也执行 maxContentLength（含未声明长度的 chunked）

    [Fact]
    public async Task StreamingResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[64 * 1024], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "chunked", c => c.Limits.MaxContentLength = 1024);
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task BufferedResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[64 * 1024], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(server.Url + "chunked", c => c.Limits.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // Cookie 容器按域共享，而不是按 origin 切开

    [Fact]
    public async Task Cookies_SetOnOneOrigin_AreSentToAnotherOrigin()
    {
        await using var authServer = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{}"),
            Headers: new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/" }));
        await using var apiServer = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions { UseCookies = true });

        await client.GetAsync(authServer.Url + "login");
        await client.GetAsync(apiServer.Url + "data");

        Assert.Contains("session=abc", apiServer.Received[0].Headers["cookie"]);
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

    // 取消异常保留底层 Cause

    [Fact]
    public async Task Cancellation_KeepsUnderlyingCause()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "slow", _ => { }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    // params 传 JsonElement 按 JSON 树展开

    [Fact]
    public void Params_WithJsonElement_AreFlattened()
    {
        using var client = new AxiosClient();
        using var document = JsonDocument.Parse("""{"a":1,"b":["x","y"],"c":{"d":true},"e":null}""");

        var uri = client.GetUri(c =>
        {
            c.Url = "http://a.com/p";
            c.Params = document.RootElement;
        });

        Assert.Equal("http://a.com/p?a=1&b[]=x&b[]=y&c[d]=true", uri);
    }

    // 池的 origin 节点数上限

    [Fact]
    public async Task Pool_EvictsIdleOrigin_WhenCapacityIsReached()
    {
        await using var first = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(first.Url + "a");
        await client.GetAsync(second.Url + "b");

        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task Pool_RejectsNewOrigin_WhenEveryNodeIsBusy()
    {
        await using var first = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(1500);
            return TestJson.Response(new { ok = true });
        });
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        var slow = client.GetAsync(first.Url + "slow");
        await WaitUntilAsync(() => first.Received.Count > 0);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(second.Url + "b"));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);

        Assert.Equal(200, (await slow).Status);
    }

    // Dispose 不能打断在途请求

    [Fact]
    public async Task Dispose_WithRequestInFlight_LetsItComplete()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(400);
            return TestJson.Response(new { ok = true });
        });

        var client = new AxiosClient();
        var pending = client.GetAsync(server.Url + "slow");
        await WaitUntilAsync(() => server.Received.Count > 0);

        client.Dispose();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    // 泛型响应的 Data 可重复读取

    [Fact]
    public async Task TypedResponse_Data_CanBeReadRepeatedly()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("hello"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "text", c => c.ResponseType = ResponseType.Text);

        Assert.Equal("hello", response.Data);
        Assert.Equal("hello", response.Data);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待测试服务器收到请求超时。");
            }

            await Task.Delay(10);
        }
    }
}
