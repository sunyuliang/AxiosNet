using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// P0：错误模型补齐（timeoutMessage / Status / ToJson / 常量 / isAxiosError / isCancel）。
/// </summary>
public sealed class ErrorModelTests
{
    [Fact]
    public async Task TimeoutMessage_OverridesDefaultText()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(600);
            return TestJson.Response(new { ok = true });
        });
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "slow",
            c =>
            {
                c.Timeout = TimeSpan.FromMilliseconds(100);
                c.TimeoutMessage = "上游响应超时，请稍后重试";
            }));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Equal("上游响应超时，请稍后重试", ex.Message);
    }

    [Fact]
    public async Task Timeout_WithoutMessage_UsesDefaultText()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(600);
            return TestJson.Response(new { ok = true });
        });
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "slow", c => c.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Contains("timeout of 100ms exceeded", ex.Message);
    }

    [Fact]
    public async Task AxiosException_ExposesStatusAndSerializableSnapshot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { message = "nope" }, statusCode: 404));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "missing"));

        Assert.Equal(404, ex.Status);
        Assert.True(ex.IsAxiosError);
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        var json = ex.ToJson();
        Assert.Equal("AxiosError", json["name"]);
        Assert.Equal("ERR_BAD_REQUEST", json["code"]);
        Assert.Equal(404, json["status"]);
        Assert.Equal("GET", json["method"]);
        Assert.EndsWith("/missing", (string)json["url"]!);
        Assert.Equal(true, json["isAxiosError"]);
    }

    [Fact]
    public void ErrorCodeConstants_MatchAxiosStrings()
    {
        Assert.Equal("ERR_BAD_REQUEST", AxiosException.ErrBadRequest);
        Assert.Equal("ERR_BAD_RESPONSE", AxiosException.ErrBadResponse);
        Assert.Equal("ECONNABORTED", AxiosException.ErrConnAborted);
        Assert.Equal("ERR_NETWORK", AxiosException.ErrNetwork);
        Assert.Equal("ERR_CANCELED", AxiosException.ErrCanceled);
        Assert.Equal("ERR_INVALID_URL", AxiosException.ErrInvalidUrl);
        Assert.Equal("ERR_BAD_OPTION_VALUE", AxiosException.ErrBadOptionValue);
    }

    [Fact]
    public async Task StaticHelpers_DetectAxiosErrorAndCancellation()
    {
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/never", cancellationToken: cts.Token));

        Assert.True(Axios.IsAxiosError(ex));
        Assert.True(Axios.IsCancel(ex));
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);

        Assert.False(Axios.IsAxiosError(new InvalidOperationException()));
        Assert.False(Axios.IsCancel(new InvalidOperationException()));
        Assert.True(Axios.IsCancel(new OperationCanceledException()));
    }
}
