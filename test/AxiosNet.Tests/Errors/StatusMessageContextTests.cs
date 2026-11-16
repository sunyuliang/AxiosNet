using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System;
using System.Net.Http;
using System.Text;

namespace AxiosNet.Tests.Errors;

/// <summary>
/// 非 2xx 异常消息的上下文后缀：请求方法 / URL（剥 query·fragment·userinfo）/ 第几次尝试。
/// 覆盖精确格式、重试递增、短路（request 为 null）、超长 URL 截断与敏感信息剥离。
/// </summary>
public sealed class StatusMessageContextTests
{
    /// <summary>空正文 500：消息精确等于「前缀 + (方法 URL, attempt 1)」，正文后缀为空不追加冒号。</summary>
    [Fact]
    public async Task EmptyBody_ExactMessage_CarriesMethodUrlAndAttempt()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "empty"));

        Assert.Equal($"Request failed with status code 500 (GET {server.Url}empty, attempt 1)", ex.Message);
    }

    /// <summary>重试 3 次：最后一次尝试的序号是 3，消息里带 (attempt 3)。</summary>
    [Fact]
    public async Task RetriedToThirdAttempt_ReportsAttemptThree()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "retry",
            c => c.Resilience.Retry.Policy = RetryPolicy.Constant(2, TimeSpan.Zero)));

        Assert.Contains(", attempt 3)", ex.Message);
    }

    /// <summary>短路响应（不发请求、request 为 null）：保持旧格式「前缀: 正文」，不带方法 / URL。</summary>
    [Fact]
    public async Task ShortCircuit_WithNullRequest_KeepsPlainFormat()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "http://shortcircuit.invalid/x",
            c => c.ShortCircuit = new AxiosShortCircuit
            {
                Status = 503,
                ContentType = "text/plain; charset=utf-8",
                Body = Encoding.UTF8.GetBytes("short"),
            }));

        Assert.Equal("Request failed with status code 503: short", ex.Message);
        Assert.DoesNotContain("GET ", ex.Message);
    }

    /// <summary>超长 URL 被截断后，attempt 序号仍在（证明先剥敏感再截断、序号不被截掉）。</summary>
    [Fact]
    public async Task OverlongUrl_StillCarriesAttempt()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + new string('a', 900)));

        Assert.Contains(", attempt 1)", ex.Message);
        Assert.Contains("truncated", ex.Message);
    }

    /// <summary>query 里的令牌不应进入日志字段 ex.Message。</summary>
    [Fact]
    public async Task SensitiveQuery_IsStrippedFromMessage()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "boom?token=SECRET123&api_key=AKIA9999"));

        Assert.DoesNotContain("SECRET123", ex.Message);
        Assert.DoesNotContain("api_key=", ex.Message);

        // 正向断言：只有「URL 确实在消息里、且 query 已被剥掉」时才算通过。
        // 缺了这条，「URL 整体缺失」也能让上面的 DoesNotContain 全绿——那不是要保证的行为。
        Assert.Contains($"{server.Url}boom", ex.Message);
        Assert.DoesNotContain("?token=", ex.Message);
    }

    /// <summary>fragment 里的令牌同样不进消息：剥掉 fragment 后连 <c>#</c> 都不该出现。</summary>
    [Fact]
    public async Task SensitiveFragment_IsStrippedFromMessage()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "frag?x=1#access_token=SECRET456"));

        Assert.DoesNotContain("SECRET456", ex.Message);
        Assert.DoesNotContain("#", ex.Message);
        Assert.Contains($"{server.Url}frag", ex.Message);
    }

    /// <summary>非 GET 方法同样带上方法名：POST 分支的消息里出现 <c>(POST </c>。</summary>
    [Fact]
    public async Task Post_MessageCarriesPostMethod()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "create",
            new { id = 1 }));

        Assert.Contains("(POST ", ex.Message);
        Assert.Contains($"{server.Url}create", ex.Message);
    }

    /// <summary>URL 里的 userinfo 与 query 都不进消息，只留 scheme + host + path。</summary>
    [Fact]
    public void UserInfo_IsStrippedFromMessage()
    {
        var config = new AxiosConfig();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://user:pass@example.test/boom?token=SECRET123");
        var response = new AxiosResponse { Status = 500, RawText = "boom" };

        var ex = AxiosException.FromResponse(config, request, response, attemptNumber: 1);

        Assert.DoesNotContain("pass", ex.Message);
        Assert.DoesNotContain("user:", ex.Message);
        Assert.DoesNotContain("SECRET123", ex.Message);
        Assert.Contains("http://example.test/boom", ex.Message);
    }
}
