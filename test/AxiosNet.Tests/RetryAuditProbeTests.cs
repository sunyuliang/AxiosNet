using System.Net;
using System.Text;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>临时探针：验证重试在「流式响应 / multipart / 自定义正文 / 超时预算」上的真实行为。</summary>
public sealed class RetryAuditProbeTests
{
    private static RetryPolicy Immediate(int maxRetries)
        => new(maxRetries, TimeSpan.Zero) { RetryNonIdempotentMethods = true };

    [Fact]
    public async Task Probe_StreamResponse_Retried()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        using var reader = new StreamReader((Stream)response.Data!);
        var text = await reader.ReadToEndAsync();

        Assert.Contains("ok", text);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Probe_MultipartFormFile_Resent()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        await client.PostAsync(
            server.Url + "u",
            new { file = new FormFile("a.txt", Encoding.UTF8.GetBytes("alpha-payload"), "text/plain") },
            c => c.ContentType = "multipart/form-data");

        Assert.Equal(2, server.Received.Count);
        Assert.Contains("alpha-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task Probe_MultipartStreamContentPart_Resent()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        var part = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("part-payload")));

        await client.PostAsync(
            server.Url + "u",
            new Dictionary<string, object?> { ["f"] = part },
            c => c.ContentType = "multipart/form-data");

        Assert.Equal(2, server.Received.Count);
        Assert.Contains("part-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task Probe_MultipartNonSeekableStreamContentPart_Resent()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        var part = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("part-payload")));

        await client.PostAsync(
            server.Url + "u",
            new Dictionary<string, object?> { ["f"] = part },
            c => c.ContentType = "multipart/form-data");

        Assert.Equal(2, server.Received.Count);
        Assert.Contains("part-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task Probe_CallerStringContent_Resent()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        await client.PostAsync(
            server.Url + "u",
            new StringContent("raw-payload", Encoding.UTF8, "text/plain"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("raw-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task Probe_StreamBodyOffset_IsPreserved()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("XXXXpayload"));
        body.Position = 4;

        await client.PostAsync(server.Url + "u", body, c => c.ContentType = "text/plain");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("payload", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task Probe_TimeoutBudget_IsPerAttempt()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            var call = Interlocked.Increment(ref calls);
            await Task.Delay(150);
            return call == 1
                ? TestJson.Response(new { error = "boom" }, statusCode: 500)
                : TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var response = await client.GetAsync(server.Url + "x", c => c.Timeout = TimeSpan.FromMilliseconds(300));

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Probe_LastAttemptError_IsSurfaced()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "first" }, statusCode: 500)
            : TestJson.Response(new { error = "second" }, statusCode: 503));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(503, ex.Status);
        Assert.Equal(2, server.Received.Count);
    }
}
