using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 请求/响应拦截器：注册顺序（请求逆序、响应顺序）、Eject、异步拦截器、
/// 响应数据改写、错误分支恢复、请求拦截器抛错透传。
/// </summary>
public sealed class InterceptorTests
{
    [Fact]
    public async Task RequestInterceptor_CanMutateConfig()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Trace", "abc-123");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal("abc-123", response.Data.GetProperty("trace").GetString());
    }

    [Fact]
    public async Task RequestInterceptors_RunInReverseRegistrationOrder()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { order = req.Headers.GetValueOrDefault("x-order") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Order", config.Headers.Get("X-Order") + "->A");
            return config;
        });
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Order", config.Headers.Get("X-Order") + "->B");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);

        // 后注册的 B 先执行，因此顺序为 B -> A
        Assert.Equal("->B->A", response.Data.GetProperty("order").GetString());
    }

    [Fact]
    public async Task RequestInterceptor_Eject_RemovesInterceptor()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        var id = client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Trace", "present");
            return config;
        });
        Assert.Equal(1, client.Interceptors.Request.Count);

        Assert.True(client.Interceptors.Request.Eject(id));
        Assert.Equal(0, client.Interceptors.Request.Count);

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Null(response.Data.GetProperty("trace").GetString());
        Assert.False(server.Received[0].Headers.ContainsKey("x-trace"));
    }

    [Fact]
    public async Task AsyncRequestInterceptor_IsAwaited()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(async config =>
        {
            await Task.Delay(20);
            config.Headers.Set("X-Trace", "async");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal("async", response.Data.GetProperty("trace").GetString());
    }

    [Fact]
    public async Task ResponseInterceptor_TransformsResponseInRegistrationOrder()
    {
        await using var server = TestHttpServer.Start(_
            => new TestResponse(Body: Encoding.UTF8.GetBytes("hello"), ContentType: "text/plain"));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(response =>
        {
            response.Data = ((string)response.Data!).ToUpperInvariant();
            return response;
        });
        client.Interceptors.Response.Use(response =>
        {
            response.Data = (string)response.Data! + "!";
            return response;
        });

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.Equal("HELLO!", response.Data);
    }

    [Fact]
    public async Task ResponseInterceptor_RejectedCanRecoverFromErrorStatus()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404, Body: TestJson.Bytes(new { error = "missing" })));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(
            onFulfilled: (AxiosResponse response) => response,
            onRejected: (Exception ex) =>
            {
                var error = Assert.IsType<AxiosException>(ex);
                Assert.Equal(404, error.Response!.Status);
                return new AxiosResponse
                {
                    Status = 200,
                    StatusText = "Recovered",
                    Data = "fallback"
                };
            });

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal(200, response.Status);
        Assert.Equal("fallback", Assert.IsType<string>(response.Data));
    }

    [Fact]
    public async Task RequestInterceptor_Throwing_PropagatesAndSkipsHttpCall()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(new Func<AxiosConfig, AxiosConfig>(
            _ => throw new InvalidOperationException("blocked")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync(server.Url));
        Assert.Equal("blocked", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptor_FulfilledSeesTypedAgnosticResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { value = 42 }));
        using var client = new AxiosClient();

        var seenStatus = 0;
        client.Interceptors.Response.Use(response =>
        {
            seenStatus = response.Status;
            return response;
        });

        await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal(200, seenStatus);
    }

    // ---------------------------------------------------------------
    // 快照缓存：无变更时复用，但 Use / Eject / Clear 必须立即可见
    // （回归：缓存若不在这些操作上失效，运行期增删拦截器会静默不生效）
    // ---------------------------------------------------------------

    [Fact]
    public async Task InterceptorSnapshotCache_InvalidatedOnEjectAndClear()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { first = req.Headers.GetValueOrDefault("x-first"), second = req.Headers.GetValueOrDefault("x-second") }));
        using var client = new AxiosClient();

        var trace = new List<string>();
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-First", "1");
            trace.Add("first");
            return config;
        });
        var secondId = client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Second", "1");
            trace.Add("second");
            return config;
        });

        // 第一次请求建立快照缓存
        await client.GetAsync(server.Url);
        Assert.Equal(2, trace.Count);

        // Eject 后：缓存必须失效，被弹出的拦截器不再执行
        client.Interceptors.Request.Eject(secondId);
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Equal(new[] { "first" }, trace);

        // Use 后：缓存必须失效，新注册的拦截器立即生效（按注册逆序执行）
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Second", "2");
            trace.Add("second-v2");
            return config;
        });
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Equal(new[] { "second-v2", "first" }, trace);

        // Clear 后：全部不再执行
        client.Interceptors.Request.Clear();
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Empty(trace);
    }

    private sealed class Profile
    {
        public int Id { get; set; }
    }

    /// <summary>
    /// 泛型请求的响应对象由管线就地填充（<c>SendTypedAsync&lt;T&gt;</c> 预先造好），
    /// 但拦截器把对象换掉时必须仍按 T 转换 —— 那时拿到的是一份全新的非泛型响应。
    /// </summary>
    [Fact]
    public async Task ResponseInterceptor_ReplacingResponse_StillConvertsDataForGenericRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 1 }));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use((AxiosResponse response) => new AxiosResponse
        {
            Data = new Profile { Id = 42 },
            Status = response.Status,
            StatusText = response.StatusText,
            Headers = response.Headers
        });

        var response = await client.GetAsync<Profile>(server.Url);
        Assert.Equal(42, response.Data!.Id);
    }

    /// <summary>
    /// 拦截器在泛型请求的响应上写入了与 T 不匹配的 Data：必须报 <c>ERR_BAD_RESPONSE</c>，
    /// 不能把检查推迟到 <c>Data</c> getter（那是裸 <see cref="InvalidCastException"/>）。
    /// </summary>
    [Fact]
    public async Task ResponseInterceptor_WritingIncompatibleData_FailsWithBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 1 }));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use((AxiosResponse response) =>
        {
            response.Data = "not a profile";
            return response;
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Profile>(server.Url));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }
}
