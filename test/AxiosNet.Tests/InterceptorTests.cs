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
}
