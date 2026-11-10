using AxiosNet.Tests.Infrastructure;
using AxiosNet.Retry;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应对象上对外开放的正文：<see cref="AxiosResponse.RawText"/>（错误体原文，始终保留）与
/// <see cref="AxiosResponse.RawBody"/>（原始字节，按 <see cref="AxiosConfig.ObservabilitySettings.CaptureRawResponseBody"/> 显式开启）。
/// </summary>
public sealed class RawResponseCaptureTests
{
    [Fact]
    public async Task SuccessfulResponse_KeepsNeitherTextNorBytes_ByDefault()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync("x");

        Assert.Null(response.RawText);
        Assert.Null(response.RawBody);
    }

    [Fact]
    public async Task CaptureRawResponseBody_KeepsTheSameBytesAsTheHook()
    {
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"张三\"}");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        byte[]? hooked = null;
        var response = await client.GetAsync<Dictionary<string, string>>("raw", c =>
        {
            c.Observability.CaptureRawResponseBody = true;
            c.Observability.OnRawResponse = (_, bytes) => hooked = bytes.ToArray();
        });

        Assert.NotNull(response.RawBody);
        Assert.Equal(payload, response.RawBody!.Value.ToArray());
        Assert.Equal(hooked, response.RawBody.Value.ToArray());

        // 2xx 不保留整段文本：RawText 只服务错误体。
        Assert.Null(response.RawText);
    }

    [Fact]
    public async Task ErrorResponse_KeepsRawText_WithoutOptIn()
    {
        var payload = Encoding.UTF8.GetBytes("missing");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            StatusCode = 404,
            Body = payload,
            ContentType = "text/plain"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("nope"));

        Assert.Equal("missing", ex.Response!.RawText);
        Assert.Null(ex.Response.RawBody);
    }

    [Fact]
    public async Task ErrorResponse_WithOptIn_KeepsBoth()
    {
        var payload = Encoding.UTF8.GetBytes("missing");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            StatusCode = 404,
            Body = payload,
            ContentType = "text/plain"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("nope", c =>
            c.Observability.CaptureRawResponseBody = true));

        Assert.Equal("missing", ex.Response!.RawText);
        Assert.Equal(payload, ex.Response.RawBody!.Value.ToArray());
    }

    [Fact]
    public async Task StreamResponse_HasNoRawBody_EvenWhenOptedIn()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes("streamed"),
            ContentType = "text/plain"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync("s", c =>
        {
            c.ResponseType = ResponseType.Stream;
            c.Observability.CaptureRawResponseBody = true;
        });

        await using var stream = (Stream)response.Data!;
        using var reader = new StreamReader(stream);

        Assert.Equal("streamed", await reader.ReadToEndAsync());

        // 流式没有完整字节，开开关也不留。
        Assert.Null(response.RawBody);
    }

    [Fact]
    public async Task ShortCircuit_ExposesRawBody_WhenOptedIn()
    {
        using var client = new AxiosClient();

        var payload = Encoding.UTF8.GetBytes("short");
        var response = await client.GetAsync<byte[]>("http://short.local/x", c =>
        {
            c.Observability.CaptureRawResponseBody = true;
            c.ShortCircuit = new AxiosShortCircuit { Body = payload, ContentType = "text/plain" };
        });

        Assert.Equal(payload, response.RawBody!.Value.ToArray());
    }

    [Fact]
    public async Task ParseFailure_OnAcceptedStatus_ExposesRawBody_WhenOptedIn()
    {
        var payload = Encoding.UTF8.GetBytes("not-json");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Dictionary<string, string>>("bad", c =>
            c.Observability.CaptureRawResponseBody = true));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Null(ex.Response!.Data);
        Assert.Equal("not-json", ex.Response.RawText);
        Assert.Equal(payload, ex.Response.RawBody!.Value.ToArray());
    }

    [Fact]
    public async Task ParseFailure_OnAcceptedStatus_KeepsRawText_WithoutOptIn()
    {
        var payload = Encoding.UTF8.GetBytes("not-json");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Dictionary<string, string>>("bad"));

        Assert.Null(ex.Response!.Data);
        Assert.Equal("not-json", ex.Response.RawText);
        Assert.Null(ex.Response.RawBody);
    }

    [Fact]
    public async Task ShortCircuit_ParseFailure_KeepsRawTextAndBody()
    {
        using var client = new AxiosClient();
        var payload = Encoding.UTF8.GetBytes("not-json");

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Dictionary<string, int>>(
            "http://short.local/x",
            c =>
            {
                c.Observability.CaptureRawResponseBody = true;
                c.ShortCircuit = new AxiosShortCircuit { Body = payload, ContentType = "application/json; charset=utf-8" };
            }));

        Assert.Null(ex.Response!.Data);
        Assert.Equal("not-json", ex.Response.RawText);
        Assert.Equal(payload, ex.Response.RawBody!.Value.ToArray());
    }

    [Fact]
    public async Task CacheHit_ParseFailure_KeepsRawTextAndBody()
    {
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosNet.Cache.AxiosCache(new AxiosNet.Cache.AxiosCacheOptions()),
        });

        await client.GetAsync("x");

        // 第二次命中缓存但不发请求：正文按本次的类型重新解析失败，原文与字节同样要能读到。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<int>("x", c =>
            c.Observability.CaptureRawResponseBody = true));

        Assert.Single(server.Received);
        Assert.Equal("{\"ok\":true}", ex.Response!.RawText);
        Assert.Equal(payload, ex.Response.RawBody!.Value.ToArray());
    }

    [Fact]
    public async Task TransformFailure_OnAcceptedStatus_KeepsRawText()
    {
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"ada\"}");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Defaults.Serialization.TransformResponse.Add((_, _) => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Dictionary<string, string>>("bad"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Null(ex.Response!.Data);
        Assert.Equal("{\"name\":\"ada\"}", ex.Response.RawText);
        Assert.Null(ex.Response.RawBody);
    }

    [Fact]
    public async Task Retry_FailureResponse_DoesNotCarryThePreviousAttemptsBody()
    {
        var attempt = 0;

        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref attempt) == 1
            ? new TestResponse { StatusCode = 500, Body = Encoding.UTF8.GetBytes("FIRST-BODY"), ContentType = "text/plain" }
            : new TestResponse { Body = Encoding.UTF8.GetBytes("not-json"), ContentType = "application/json; charset=utf-8" });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience = { Retry = { Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero } } },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Dictionary<string, int>>("retry", c =>
            c.Observability.CaptureRawResponseBody = true));

        Assert.Equal(2, attempt);
        Assert.Equal(200, ex.Response!.Status);
        Assert.Equal("not-json", ex.Response.RawText);
        Assert.Equal(Encoding.UTF8.GetBytes("not-json"), ex.Response.RawBody!.Value.ToArray());

        // 正文大小同样是本次尝试的，不是上一次的 "FIRST-BODY"（10 字节）。
        Assert.Equal(Encoding.UTF8.GetBytes("not-json").Length, ex.Response.BodyBytes);
    }

    [Fact]
    public async Task Failover_MirrorSuccess_DoesNotKeepThePrimaryEndpointError()
    {
        var mirrorPayload = Encoding.UTF8.GetBytes("{\"mirror\":true}");

        await using var primary = TestHttpServer.Start(_ => new TestResponse
        {
            StatusCode = 500,
            Body = Encoding.UTF8.GetBytes("PRIMARY-BODY"),
            ContentType = "text/plain"
        });

        await using var mirror = TestHttpServer.Start(_ => new TestResponse
        {
            Body = mirrorPayload,
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Observability = { CaptureRawResponseBody = true },
            Resilience = { Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } } },
        });

        var response = await client.GetAsync<Dictionary<string, bool>>("x");

        // 换端点成功：原文 / 字节 / 字节数全部是镜像这一份，不残留主端点 500 的正文。
        Assert.Equal(200, response.Status);
        Assert.True(response.Data!["mirror"]);
        Assert.Null(response.RawText);
        Assert.Equal(mirrorPayload, response.RawBody!.Value.ToArray());
        Assert.Equal(mirrorPayload.Length, response.BodyBytes);
    }

    [Fact]
    public async Task NotModified_Revalidation_ExposesStoredBody_WhenOptedIn()
    {
        var payload = TestJson.Bytes(new Person("ada", 36));

        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("If-None-Match", out var etag) && etag == "\"v1\""
                ? new TestResponse(304, Headers: HeaderValues(("ETag", "\"v1\"")))
                : TestJson.Response(new Person("ada", 36), headers: HeaderValues(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosNet.Cache.AxiosCache(new AxiosNet.Cache.AxiosCacheOptions
            {
                TimeToLive = TimeSpan.FromMilliseconds(50)
            }),
            Observability = { CaptureRawResponseBody = true },
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);

        var revalidated = await client.GetAsync<Person>(server.Url + "x");

        // 304 没有正文：RawBody 是缓存里的那一份，与 Data 同源。
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(200, revalidated.Status);
        Assert.Equal("ada", revalidated.Data!.Name);
        Assert.Null(revalidated.RawText);
        Assert.Equal(payload, revalidated.RawBody!.Value.ToArray());
        Assert.Equal(payload.Length, revalidated.BodyBytes);
    }

    [Fact]
    public async Task SingleFlightSharedFailure_ExposesRawTextAndBody_ToFollower()
    {
        var payload = Encoding.UTF8.GetBytes("missing");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await using var server = TestHttpServer.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new TestResponse { StatusCode = 404, Body = payload, ContentType = "text/plain" };
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosNet.Cache.AxiosCache(new AxiosNet.Cache.AxiosCacheOptions()),
            Observability = { CaptureRawResponseBody = true },
        });

        var leader = client.GetAsync("x");

        for (var i = 0; i < 200 && Volatile.Read(ref calls) == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Volatile.Read(ref calls));

        var follower = client.GetAsync("x");
        await Task.Delay(100);
        release.SetResult(true);

        var errors = await Task.WhenAll(CaptureFailure(leader), CaptureFailure(follower));

        // 共享的是领导者的错误正文：跟随者没发请求，但原文与字节一样能读到。
        Assert.Single(server.Received);

        foreach (var error in errors)
        {
            Assert.Equal(404, error.Response!.Status);
            Assert.Equal("missing", error.Response.RawText);
            Assert.Equal(payload, error.Response.RawBody!.Value.ToArray());
        }

        static async Task<AxiosException> CaptureFailure(Task<AxiosResponse> task)
            => await Assert.ThrowsAsync<AxiosException>(() => task);
    }

    [Fact]
    public async Task RejectedStatus_WithCaching_ReleasesTheInternalStoreBuffer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
            ContentType = "application/json; charset=utf-8"
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosNet.Cache.AxiosCache(new AxiosNet.Cache.AxiosCacheOptions()),
        });

        // 状态码被拒：这条响应不会落库，内部正文缓冲不该跟着异常一起存活。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x", c =>
            c.ValidateStatus = status => status == 201));

        Assert.Equal(200, ex.Response!.Status);
        Assert.Equal("{\"ok\":true}", ex.Response.RawText);
        Assert.Null(ex.Response.RawBodyBuffer);
    }

    private static Dictionary<string, string> HeaderValues(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs)
        {
            headers[name] = value;
        }

        return headers;
    }

    [Fact]
    public async Task InstanceDefault_IsInherited_AndCanBeTurnedOffPerRequest()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { CaptureRawResponseBody = true },
        });

        var inherited = await client.GetAsync("x");
        var cleared = await client.GetAsync("x", c => c.Observability.CaptureRawResponseBody = false);

        Assert.NotNull(inherited.RawBody);
        Assert.Null(cleared.RawBody);
    }

    private static TestResponse Json() => new()
    {
        Body = Encoding.UTF8.GetBytes("{\"ok\":true}"),
        ContentType = "application/json; charset=utf-8"
    };
}
