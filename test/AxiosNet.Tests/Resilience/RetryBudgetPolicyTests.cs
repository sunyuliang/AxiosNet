using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 全局重试预算（<see cref="RetryBudgetPolicy"/>）：令牌桶跨请求共享、只拦重试不拦首发、内层判定不被预算绕过。
/// </summary>
public sealed class RetryBudgetPolicyTests
{
    [Fact]
    public async Task ExhaustedBudget_StopsRetries_AndSurfacesTheRealFailure()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 5), capacity: 2, refillPerSecond: 0);
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 预算只决定「还重不重试」：放弃重试后抛的是真实失败（5xx），不是某种「预算耗尽」的新错误码
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(503, ex.Status);

        // 1 次首发 + 2 次重试（把桶用光）；第 3 次重试被预算拒掉
        Assert.Equal(3, server.Received.Count);
        Assert.Equal(1, policy.RefusedRetries);
    }

    [Fact]
    public async Task Budget_IsSharedAcrossIndependentRequests()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 5), capacity: 1, refillPerSecond: 0);
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        // 第一条：首发 + 1 次重试（用掉唯一的令牌），第 2 次重试被拒
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "a"));
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(1, policy.RefusedRetries);

        // 第二条独立请求：桶已空，首发失败后直接放弃重试（预算真的是全实例共享的）
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "b"));
        Assert.Equal(3, server.Received.Count);
        Assert.Equal(2, policy.RefusedRetries);
    }

    [Fact]
    public async Task NonRetryableFailure_DoesNotConsumeBudget()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 400));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 3), capacity: 1, refillPerSecond: 0);
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        // 内层说「不重试」的失败不扣令牌：预算只度量重试，不度量请求
        Assert.Equal(0, policy.RefusedRetries);
        Assert.Equal(1d, policy.AvailableTokens);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Refill_RestoresBudgetOverTime()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 1), capacity: 1, refillPerSecond: 100);
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        // 第一条把唯一的令牌用掉（首发 + 1 次重试；maxRetries = 1 保证不会再多发）
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "a"));
        Assert.Equal(2, server.Received.Count);

        // 100 次/秒 × 50ms 够补满 1 个令牌（按真实经过时间补，余量很大，不依赖精确计时）；
        // 不在请求结束处断言「桶已空」—— 100 次/秒下 10ms 就补回令牌，那条断言本身会随时序抖动。
        await Task.Delay(50);
        Assert.True(policy.AvailableTokens >= 1d, $"available = {policy.AvailableTokens}");

        // 补回来的令牌又能放行一次重试：第二条请求有 2 次发送
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "b"));
        Assert.Equal(4, server.Received.Count);
    }

    [Fact]
    public async Task OnBudgetExhausted_ReportsTheRefusedAttempt()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 5), capacity: 1, refillPerSecond: 0);
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        var refused = new List<AxiosRetryContext>();
        policy.OnBudgetExhausted = refused.Add;

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 第一次重试放行（令牌），第二次失败时被拒：上报的就是第 2 次尝试
        var context = Assert.Single(refused);
        Assert.Equal(2, context.Attempt);
        Assert.Equal(AxiosException.ErrBadResponse, context.ErrorCode);
        Assert.Equal(503, context.StatusCode);
    }

    [Fact]
    public async Task CallbackThrowing_RefundsTheReservedToken()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(Immediate(maxRetries: 3), capacity: 1, refillPerSecond: 0);

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = policy,
                OnRetry = _ => throw new InvalidOperationException("callback boom")
            }
        });

        // 回调抛错：重试这一发没有发出去，令牌必须退回，否则共享桶被白白耗掉
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(1d, policy.AvailableTokens);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task BackoffCanceled_RefundsTheReservedToken()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var policy = new RetryBudgetPolicy(
            RetryPolicy.Constant(3, TimeSpan.FromSeconds(5)),
            capacity: 1,
            refillPerSecond: 0);

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });
        using var cts = new CancellationTokenSource();

        var pending = client.GetAsync(server.Url + "x", c => c.CancellationToken = cts.Token);

        // 等退避真的开始，再取消：这一次重试同样没有发出去
        await Task.Delay(150);
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => pending);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Equal(1d, policy.AvailableTokens);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task NestedBudget_RefundCascadesToInnerBucket()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));

        // 预算包预算：一次重试两层各扣一个令牌，退费也必须两层一起退，否则内层桶被单边耗掉。
        var inner = new RetryBudgetPolicy(Immediate(maxRetries: 1), capacity: 1, refillPerSecond: 0);
        var outer = new RetryBudgetPolicy(inner, capacity: 1, refillPerSecond: 0);

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = outer,
                OnRetry = _ => throw new InvalidOperationException("callback boom")
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(1d, outer.AvailableTokens);
        Assert.Equal(1d, inner.AvailableTokens);
    }

    [Fact]
    public void InnerPolicy_ControlsShapeAndDelay()
    {
        var inner = RetryPolicy.Constant(7, TimeSpan.FromMilliseconds(123));
        var policy = new RetryBudgetPolicy(inner, capacity: 10, refillPerSecond: 1);

        Assert.Same(inner, policy.Inner);
        Assert.Equal(7, policy.MaxRetries);

        var context = new AxiosRetryContext(1, 7, new HttpRequestException("boom"));
        Assert.Equal(TimeSpan.FromMilliseconds(123), policy.GetDelay(context));
    }

    [Fact]
    public void Constructor_RejectsUnusableBudgets()
    {
        var inner = Immediate(maxRetries: 1);

        Assert.Throws<ArgumentNullException>(() => new RetryBudgetPolicy(null!, capacity: 1, refillPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetPolicy(inner, capacity: 0, refillPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetPolicy(inner, capacity: 1, refillPerSecond: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetPolicy(inner, capacity: 1, refillPerSecond: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudgetPolicy(inner, capacity: 1, refillPerSecond: double.PositiveInfinity));
    }

    private static RetryPolicy Immediate(int maxRetries) => RetryPolicy.Constant(maxRetries, TimeSpan.Zero);
}
