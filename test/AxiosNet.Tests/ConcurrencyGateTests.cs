using System.Diagnostics;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 在途并发闸门（<c>Limits.MaxConcurrentRequests</c> / <c>Limits.QueueTimeout</c>）。
/// <para>
/// 锁定的契约：并发上限按实例生效（与按字节计速的 <c>MaxRate</c> 正交）、排队可取消、
/// 排队超时给 <c>ERR_QUEUE_TIMEOUT</c>（不是 <c>ECONNABORTED</c>）、排队时间不计入 <c>Timeout</c>、
/// 流式响应的槽位占到正文流释放为止、按请求写不同上限报 <c>ERR_BAD_OPTION_VALUE</c>。
/// </para>
/// </summary>
public sealed class ConcurrencyGateTests
{
    /// <summary>让服务端每个请求都停一会儿：没有闸门时并发数会明显超过 1。</summary>
    private static TestHttpServer SlowServer(int delayMs = 80)
        => TestHttpServer.Start(async _ =>
        {
            await Task.Delay(delayMs);
            return TestJson.Response(new { ok = true });
        });

    /// <summary>轮询等待某个条件成立（测试里等「第一条请求已经占住槽位」这类状态）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"等待超时：{what}");
            }

            await Task.Delay(10);
        }
    }

    private static AxiosClient LimitedClient(int maxConcurrency, TimeSpan? queueTimeout = null, TimeSpan? timeout = null)
    {
        var config = new AxiosConfig
        {
            Limits = new() { MaxConcurrentRequests = maxConcurrency, QueueTimeout = queueTimeout }
        };

        if (timeout is { } value)
        {
            config.Timeout = value;
        }

        return new AxiosClient(config);
    }

    [Fact]
    public async Task WithoutLimit_RequestsRunConcurrently()
    {
        await using var server = SlowServer();
        using var client = new AxiosClient();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetAsync(server.Url + "x")));

        // 基线：不设上限时并发确实会叠起来（否则下面的「被压到 2」证明不了任何东西）
        Assert.True(server.PeakConcurrentRequests > 2, $"peak = {server.PeakConcurrentRequests}");
    }

    [Fact]
    public async Task MaxConcurrentRequests_CapsInFlightRequests()
    {
        await using var server = SlowServer();
        using var client = LimitedClient(maxConcurrency: 2);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync(server.Url + "x")));

        Assert.All(responses, r => Assert.Equal(200, r.Status));
        Assert.Equal(8, server.Received.Count);
        Assert.True(server.PeakConcurrentRequests <= 2, $"peak = {server.PeakConcurrentRequests}");
    }

    [Fact]
    public async Task QueueTimeout_WhenSlotsBusy_ThrowsQueueTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        using var client = LimitedClient(maxConcurrency: 1);

        var occupying = client.GetAsync(server.Url + "hold");
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "queued", c => c.Limits.QueueTimeout = TimeSpan.FromMilliseconds(100)));

        // 队列超时不是请求超时：请求根本没发出去，处置方式（降速 / 扩容 / 退避）完全不同
        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);
        Assert.NotEqual(AxiosException.ErrConnAborted, ex.Code);
        Assert.Contains("MaxConcurrentRequests = 1", ex.Message);

        // 请求没发出去：错误里带的是待发请求（用于定位是哪条请求没排上队）
        Assert.Equal(server.Url + "queued", ex.Request?.RequestUri?.ToString());

        // 排队的请求没发出去：服务端只见过第一条
        Assert.Single(server.Received);

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task QueueTimeout_Zero_DoesNotWait()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        using var client = LimitedClient(maxConcurrency: 1);
        var occupying = client.GetAsync(server.Url + "hold");
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "queued", c => c.Limits.QueueTimeout = TimeSpan.Zero));
        stopwatch.Stop();

        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"耗时 {stopwatch.ElapsedMilliseconds}ms");

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task WaitingForSlot_IsCancelable()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        using var client = LimitedClient(maxConcurrency: 1);
        var occupying = client.GetAsync(server.Url + "hold");
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        using var cts = new CancellationTokenSource();
        var queued = client.GetAsync(server.Url + "queued", cancellationToken: cts.Token);
        await Task.Delay(80);
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => queued);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.True(Axios.IsCancel(ex));

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task QueueWait_IsNotCountedAgainstTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            // 第一条占住槽位 300ms：超过下面的 Timeout（200ms）
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        // Timeout 只覆盖「真正发出去」的那一段；排队有 QueueTimeout 单独兜底
        using var client = LimitedClient(maxConcurrency: 1, queueTimeout: TimeSpan.FromSeconds(5), timeout: TimeSpan.FromMilliseconds(200));

        // 占位的那条自己要跑 300ms（超过实例 Timeout），按请求关掉它的超时，只让排队那条受 200ms 约束
        var occupying = client.GetAsync(server.Url + "hold", c => c.Timeout = TimeSpan.Zero);
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        var queued = client.GetAsync(server.Url + "queued");
        await Task.Delay(300);
        release.SetResult(true);

        await occupying;
        var response = await queued;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task StreamResponse_HoldsSlotUntilStreamDisposed()
    {
        var payload = new byte[64 * 1024];
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        using var client = LimitedClient(maxConcurrency: 1);

        var response = await client.GetAsync<Stream>(server.Url + "file");
        var body = response.Data!;
        try
        {
            // 正文还没读完：槽位仍被这条下载占着
            var ex = await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync(server.Url + "other", c => c.Limits.QueueTimeout = TimeSpan.FromMilliseconds(100)));
            Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);
        }
        finally
        {
            await body.DisposeAsync();
        }

        // 释放正文流即归还槽位
        var after = await client.GetAsync(server.Url + "after", c => c.Limits.QueueTimeout = TimeSpan.FromSeconds(5));
        Assert.Equal(200, after.Status);
    }

    [Fact]
    public async Task PerRequestDifferentLimit_ThrowsBadOptionValue()
    {
        await using var server = SlowServer(delayMs: 0);
        using var client = LimitedClient(maxConcurrency: 2);

        // 先跑一条，确保闸门已按 2 建出来
        await client.GetAsync(server.Url + "warmup");

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Limits.MaxConcurrentRequests = 8));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("MaxConcurrentRequests", ex.Message);

        // 复述实例的值放行（与 Proxy / MaxRedirects 同一策略）
        var ok = await client.GetAsync(server.Url + "x", c => c.Limits.MaxConcurrentRequests = 2);
        Assert.Equal(200, ok.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidMaxConcurrentRequests_ThrowsBadOptionValue(int value)
    {
        await using var server = SlowServer(delayMs: 0);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Limits.MaxConcurrentRequests = value));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task NegativeQueueTimeout_ThrowsBadOptionValue()
    {
        await using var server = SlowServer(delayMs: 0);
        using var client = LimitedClient(maxConcurrency: 1);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Limits.QueueTimeout = TimeSpan.FromMilliseconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task DefaultsSetAfterConstruction_StillTakesEffect()
    {
        await using var server = SlowServer();
        using var client = new AxiosClient();

        // 无上限时先跑一条：闸门是「首个受限请求」才建的，不该被这条提前固化
        await client.GetAsync(server.Url + "before");

        client.Defaults.Limits.MaxConcurrentRequests = 1;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetAsync(server.Url + "x")));

        // 后 4 条被压到串行（峰值可能仍是 before 那一条留下的 1）
        Assert.True(server.PeakConcurrentRequests <= 1, $"peak = {server.PeakConcurrentRequests}");
    }

    [Fact]
    public async Task ReplayingConfigFromAnotherInstance_KeepsTargetGate()
    {
        await using var server = SlowServer(delayMs: 0);

        // 来源实例：上限 1；目标实例：上限 2
        using var source = LimitedClient(maxConcurrency: 1);
        using var target = LimitedClient(maxConcurrency: 2);

        var first = await source.GetAsync(server.Url + "x");

        // 回放生效配置不该把来源实例的闸门搬过来（否则要么凭空加闸，要么撞上「上限不同」）
        var replayed = await target.RequestAsync(first.Config!);
        Assert.Equal(200, replayed.Status);

        // 目标实例的闸门仍是 2：可以复述、写 1 会报错
        await target.GetAsync(server.Url + "x", c => c.Limits.MaxConcurrentRequests = 2);
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            target.GetAsync(server.Url + "x", c => c.Limits.MaxConcurrentRequests = 1));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task QueueTimeout_IsNotRetriedByDefaultPolicy()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        using var client = LimitedClient(maxConcurrency: 1);
        var retries = 0;

        var occupying = client.GetAsync(server.Url + "hold");
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "queued", c =>
            {
                c.Limits.QueueTimeout = TimeSpan.FromMilliseconds(50);
                c.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.Zero);
                c.Retry.OnRetry = _ => Interlocked.Increment(ref retries);
            }));

        // 排队超时不是「服务端失败」，默认策略的可重试集合里没有它：
        // 请求没发出去，盲目重发只会把等待时间成倍拉长。
        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);
        Assert.Equal(0, retries);

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task RaisedLimitAfterGateCreated_ThrowsInsteadOfSilentlyIgnoring()
    {
        await using var server = SlowServer(delayMs: 0);
        using var client = LimitedClient(maxConcurrency: 1);

        await client.GetAsync(server.Url + "warmup");

        client.Defaults.Limits.MaxConcurrentRequests = 4;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }
}
