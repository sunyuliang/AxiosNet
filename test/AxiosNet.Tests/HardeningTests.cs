using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 全局代码审查后的加固用例：每条对应一处已复现的缺陷，修复前必然是红的。
/// </summary>
public sealed class HardeningTests
{
    // ---------------------------------------------------------------
    // P0-1：请求头 CRLF 注入（CWE-113）
    // ---------------------------------------------------------------

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

    // ---------------------------------------------------------------
    // P0-2：poolOptions.MaxAutomaticRedirections 被未显式设置的默认值覆盖
    // ---------------------------------------------------------------

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
            new AxiosConfig { MaxRedirects = 12 },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.Equal(12, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void MaxRedirectsZero_DisablesRedirect_ButKeepsPoolLimit()
    {
        using var client = new AxiosClient(
            new AxiosConfig { MaxRedirects = 0 },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 10 });

        Assert.False(client.Pool!.Options.AllowAutoRedirect);
        Assert.Equal(10, client.Pool!.Options.MaxAutomaticRedirections);
    }

    [Fact]
    public void NegativeMaxRedirects_ThrowsBadOptionValue()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(new AxiosConfig { MaxRedirects = -1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ---------------------------------------------------------------
    // P0-3：流式正文截断必须与非流式同错误模型（ERR_NETWORK）
    // ---------------------------------------------------------------

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

    /// <summary>Timeout=0（没有超时预算）时的链路也必须做异常翻译，不能因为配置不同给出两种异常类型。</summary>
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

    // ---------------------------------------------------------------
    // P0-4：超大 Timeout 收敛，而不是抛裸 ArgumentOutOfRangeException
    // ---------------------------------------------------------------

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
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"ok\":true}")));
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

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/x", c => c.Timeout = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task NegativeMaxRateAndLimits_ThrowBadOptionValue()
    {
        using var client = new AxiosClient();

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.MaxRate = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.MaxContentLength = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.MaxBodyLength = -1))).Code);
    }

    // ---------------------------------------------------------------
    // P1：params 不能落到 URL fragment 里
    // ---------------------------------------------------------------

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

    // ---------------------------------------------------------------
    // P1：PostAsync(url, null) 不能落到 AxiosConfig 重载上炸掉
    // ---------------------------------------------------------------

    [Fact]
    public void PostAsync_WithNullConfig_ThrowsArgumentNullException()
    {
        using var client = new AxiosClient();

        // 重载决议：AxiosConfig 比 object 更具体，`PostAsync(url, null)` 会走到这个重载。
        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = client.PostAsync("http://127.0.0.1:1/x", (AxiosConfig)null!);
        });

        Assert.Equal("config", ex.ParamName);
    }

    [Fact]
    public async Task PostAsync_WithNullBody_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "ping", (object?)null);

        Assert.Equal(200, response.Status);
    }

    // ---------------------------------------------------------------
    // P1：流式响应也必须执行 maxContentLength（含未声明长度的 chunked）
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamingResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[64 * 1024],
            ContentType: "application/octet-stream",
            Chunked: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "chunked", c => c.MaxContentLength = 1024);
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task BufferedResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[64 * 1024],
            ContentType: "application/octet-stream",
            Chunked: true));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<byte[]>(server.Url + "chunked", c => c.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // P2：Cookie 容器按域共享，而不是按 origin（端口）切开
    // ---------------------------------------------------------------

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

    // ---------------------------------------------------------------
    // P2：实例级配置按请求写「与生效值相同」的值等价于不写，放行；写不同的值才报错
    // ---------------------------------------------------------------

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToEffectiveValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { MaxRedirects = 5 });

        // 把 defaults 抄一遍到每个请求上是常见写法：值相同就是等价写法，不该炸
        var response = await client.GetAsync(server.Url + "x", c => c.MaxRedirects = 5);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    // ---------------------------------------------------------------
    // P2：取消异常保留底层 Cause
    // ---------------------------------------------------------------

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

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "slow", _ => { }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    // ---------------------------------------------------------------
    // P2：params 传 JsonElement 必须按 JSON 树展开
    // ---------------------------------------------------------------

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

    // ---------------------------------------------------------------
    // P2：池的 origin 节点数上限
    // ---------------------------------------------------------------

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

        // 在途请求不受容量检查影响
        Assert.Equal(200, (await slow).Status);
    }

    // ---------------------------------------------------------------
    // P2：Dispose 不能打断在途请求（优雅关闭）
    // ---------------------------------------------------------------

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

    // ---------------------------------------------------------------
    // P2：泛型响应的 Data getter 不再每次都重跑转换
    // ---------------------------------------------------------------

    [Fact]
    public async Task TypedResponse_Data_CanBeReadRepeatedly()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("hello"),
            ContentType: "text/plain; charset=utf-8"));

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
