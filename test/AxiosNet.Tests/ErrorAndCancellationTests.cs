using System.Diagnostics;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 错误语义与 axios 对齐：4xx/5xx 错误码、validateStatus、ECONNABORTED 超时、ERR_CANCELED 取消。
/// </summary>
public sealed class ErrorAndCancellationTests
{
    [Fact]
    public async Task Status404_ThrowsAxiosException_WithResponseAndBadRequestCode()
    {
        await using var server = TestHttpServer.Start(_
            => new TestResponse(404, Body: TestJson.Bytes(new { error = "missing" })));

        using var client = new AxiosClient();
        var config = new AxiosConfig { Url = server.Url + "missing" };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(config));

        Assert.True(ex.IsAxiosError);
        Assert.Equal("ERR_BAD_REQUEST", ex.Code);
        Assert.Equal(404, ex.Response!.Status);
        Assert.Equal("Not Found", ex.Response.StatusText);
        Assert.Equal("missing", ex.Response.Data is JsonElement el ? el.GetProperty("error").GetString() : null);
        Assert.NotNull(ex.Request);
        Assert.Equal(config.Url, ex.Config!.Url);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Status500_UsesBadResponseCode()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal("ERR_BAD_RESPONSE", ex.Code);
        Assert.Equal(500, ex.Response!.Status);
    }

    [Fact]
    public async Task ValidateStatus_True_AcceptsAnyStatusCode()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(503));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ValidateStatus = _ => true);

        Assert.Equal(503, response.Status);
    }

    [Fact]
    public async Task ValidateStatus_CustomRule_IsRespected()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404));
        using var client = new AxiosClient();

        // 只接受 404
        var response = await client.GetAsync(server.Url, c => c.ValidateStatus = status => status == 404);
        Assert.Equal(404, response.Status);

        await using var server2 = TestHttpServer.Start(_ => new TestResponse(400));
        await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server2.Url, c => c.ValidateStatus = status => status == 404));
    }

    [Fact]
    public async Task Timeout_ThrowsECONNABORTED_Quickly()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromMilliseconds(300)));

        stopwatch.Stop();

        Assert.Equal("ECONNABORTED", ex.Code);
        Assert.Contains("timeout", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"实际耗时 {stopwatch.Elapsed}，超时未及时生效");
    }

    [Fact]
    public async Task ExternalCancellation_ThrowsERRCANCELED()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, cancellationToken: cts.Token));

        Assert.Equal("ERR_CANCELED", ex.Code);
    }

    [Fact]
    public async Task NetworkError_UsesERRNETWORKCode()
    {
        using var client = new AxiosClient();

        // 未被监听的端口，连接会被拒绝
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/unreachable", c => c.Timeout = TimeSpan.FromSeconds(5)));

        Assert.Equal("ERR_NETWORK", ex.Code);
        Assert.NotNull(ex.InnerException);
    }
}
