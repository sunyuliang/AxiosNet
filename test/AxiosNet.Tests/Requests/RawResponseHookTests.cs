using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 原始响应字节钩子（<see cref="AxiosConfig.CallbackSettings.OnRawResponse"/>）：
/// 触发时点、能看到的上下文、错误状态下的可见性，以及「不触发」的两种情形（流式 / 短路）。
/// </summary>
public sealed class RawResponseHookTests
{
    [Fact]
    public async Task FiresBeforeDeserialization_WithStatusHeadersAndRawBytes()
    {
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"张三\"}");

        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = payload,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["X-Trace"] = "abc123" }
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        byte[]? seenBytes = null;
        AxiosResponseInfo? seenContext = null;
        var order = new List<string>();

        var response = await client.GetAsync<Dictionary<string, string>>("raw", c =>
        {
            c.Callbacks.OnRawResponse = (info, bytes) =>
            {
                order.Add("raw");
                seenContext = info;
                seenBytes = bytes.ToArray();
            };

            c.TransformResponse.Add((data, _) =>
            {
                order.Add("transform");
                return data;
            });
        });

        // 顺序：原始字节 → 反序列化 → transformResponse。
        Assert.Equal(new[] { "raw", "transform" }, order);
        Assert.Equal(payload, seenBytes);
        Assert.Equal("张三", response.Data!["name"]);

        Assert.NotNull(seenContext);
        Assert.Equal(200, seenContext!.Status);
        Assert.Equal("application/json", seenContext.MediaType);
        Assert.Equal("abc123", seenContext.GetHeader("X-Trace"));

        // 上下文与响应共用同一份头视图。
        Assert.Same(response.Headers, seenContext.Headers);
    }

    [Fact]
    public async Task FiresForErrorStatus_BeforeValidateStatusRejects()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            StatusCode = 404,
            Body = Encoding.UTF8.GetBytes("missing"),
            ContentType = "text/plain"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var seenStatus = 0;
        string? seenBody = null;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("nope", c =>
            c.Callbacks.OnRawResponse = (info, bytes) =>
            {
                seenStatus = info.Status;
                seenBody = Encoding.UTF8.GetString(bytes.Span);
            }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        // 4xx 也触发：钩子在状态码校验之前，错误体对审计同样有用。
        Assert.Equal(404, seenStatus);
        Assert.Equal("missing", seenBody);
    }

    [Fact]
    public async Task CallbackThrows_ReportsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes("{}"),
            ContentType = "application/json"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Callbacks.OnRawResponse = (_, _) => throw new InvalidOperationException("boom")));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("OnRawResponse", ex.Message);
    }

    [Fact]
    public async Task NotFiredForStreamResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes("streamed"),
            ContentType = "text/plain"
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var fired = false;
        var response = await client.GetAsync("s", c =>
        {
            c.ResponseType = ResponseType.Stream;
            c.Callbacks.OnRawResponse = (_, _) => fired = true;
        });

        await using var stream = (Stream)response.Data!;
        using var reader = new StreamReader(stream);

        Assert.Equal("streamed", await reader.ReadToEndAsync());
        Assert.False(fired);
    }

    [Fact]
    public async Task NotFiredForShortCircuit()
    {
        using var client = new AxiosClient();

        var fired = false;
        var response = await client.GetAsync<byte[]>("http://short.local/x", c =>
        {
            c.ShortCircuit = AxiosShortCircuit.Json("{\"ok\":true}");
            c.Callbacks.OnRawResponse = (_, _) => fired = true;
        });

        Assert.NotEmpty(response.Data!);
        Assert.False(fired);
    }

    [Fact]
    public async Task FiresOncePerAttempt_AcrossRetries()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 2
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = { Policy = new RetryPolicy(1, TimeSpan.Zero) }
        });

        var seenStatuses = new List<int>();
        var response = await client.GetAsync("retry", c =>
            c.Callbacks.OnRawResponse = (info, _) => seenStatuses.Add(info.Status));

        Assert.Equal(200, response.Status);

        // 每次尝试各读一份正文，失败的那次（500）同样触发。
        Assert.Equal(new[] { 500, 200 }, seenStatuses);
    }

    [Fact]
    public async Task PerRequestCallback_ReplacesDefault_OtherwiseDefaultApplies()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes("{}"),
            ContentType = "application/json"
        });

        var defaultCalls = 0;
        var perCallCalls = 0;

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Callbacks = new AxiosConfig.CallbackSettings
            {
                OnRawResponse = (_, _) => defaultCalls++
            }
        });

        await client.GetAsync("a");
        Assert.Equal(1, defaultCalls);

        await client.GetAsync("b", c => c.Callbacks.OnRawResponse = (_, _) => perCallCalls++);

        // 按请求写的那份整体覆盖实例默认，不叠加执行。
        Assert.Equal(1, defaultCalls);
        Assert.Equal(1, perCallCalls);
    }
}
