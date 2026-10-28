using System.Diagnostics;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 总预算（<see cref="AxiosConfig.Deadline"/>）：整条逻辑请求的上限，跨重试 / 端点 / 对冲共享。
/// <para>
/// 断言的是「N 毫秒内必须被打断」，与其它集合并行跑会被抢走 CPU，故进 <see cref="TimingCollection"/>。
/// </para>
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class DeadlineTests
{
    // ------------------------------------------------------------------
    // 单次尝试：总预算比 Timeout 小的时候，实际上限是「剩余总预算」
    // ------------------------------------------------------------------

    [Fact]
    public async Task Deadline_ClampsAttemptTimeout_AndSaysSoInMessage()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "slow", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);              // 单次尝试给得很宽
            c.Deadline = TimeSpan.FromMilliseconds(150);       // 但整条请求只允许 150ms
        }));

        watch.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);

        // 真实上限是 150ms 而不是 30s：消息必须说清是总预算压小的，否则调用方会以为 Timeout 没生效。
        Assert.Contains("remaining Deadline budget", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"实际耗时 {watch.Elapsed}：总预算没有生效");
    }

    // ------------------------------------------------------------------
    // 重试：总预算在多次尝试之间共享，用尽后下一次尝试根本不发出
    // ------------------------------------------------------------------

    [Fact]
    public async Task Deadline_CutsRetryLoopShort()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ =>
        {
            Interlocked.Increment(ref calls);
            return TestJson.Response(new { error = "boom" }, statusCode: 500) with { BodyDelay = TimeSpan.FromMilliseconds(80) };
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(20, TimeSpan.Zero) }
        });

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "boom", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.Deadline = TimeSpan.FromMilliseconds(200);
        }));
        watch.Stop();

        // 总预算用尽后由「下一次尝试开始」报出：错误码与超时一致，消息指明第几次没发出。
        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(ex.IsDeadlineExceeded);   // 公开口径：与「发出后超时」共用 ECONNABORTED，靠这一位区分
        Assert.Contains("deadline of", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could be sent", ex.Message, StringComparison.Ordinal);

        // 20 次重试一次都不该发出：实际发送次数必须远小于 MaxRetries + 1。
        Assert.InRange(calls, 1, 6);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"实际耗时 {watch.Elapsed}：总预算没有切断重试");
    }

    // ------------------------------------------------------------------
    // 排队：Deadline 计入排队时间（与 QueueTimeout 的口径不同）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Deadline_CountsTimeSpentWaitingForConcurrencySlot()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await gate.Task;
            return TestJson.Response(new { ok = true });
        });

        // 只能有一条在途：第二条请求注定要排队
        using var client = new AxiosClient(new AxiosConfig { Limits = { MaxConcurrentRequests = 1 } });

        // ① 占住唯一的槽位（等它真的到达服务端，否则第二条可能先抢到槽位）
        var holder = client.GetAsync(server.Url + "holder");
        var waitForHolder = Stopwatch.StartNew();
        while (waitForHolder.Elapsed < TimeSpan.FromSeconds(5) && server.Received.Count == 0)
        {
            await Task.Delay(5);
        }

        Assert.Single(server.Received);

        // ② 第二条请求排队期间总预算就到点了：它连发送阶段都不该进
        var queued = client.GetAsync(server.Url + "queued", c => c.Deadline = TimeSpan.FromMilliseconds(120));

        // 让总预算在「排队中」过掉（排队时间计入 Deadline，与 QueueTimeout 的口径不同）
        await Task.Delay(300);

        gate.SetResult();
        await holder;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => queued);

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Contains("deadline of", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Received.Count(request => request.Path == "/holder"));
        Assert.Equal(0, server.Received.Count(request => request.Path == "/queued"));
    }

    // 预算必须在**排队时**就把等待截断：否则 QueueTimeout 不设（默认一直等）时，
    // Deadline=150ms 的请求会一直排到槽位空出来，预算成了「只记账、不执行」。
    [Fact]
    public async Task Deadline_CutsQueueWaitShort_WithoutWaitingForTheSlot()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await gate.Task;
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { Limits = { MaxConcurrentRequests = 1 } });

        var holder = client.GetAsync(server.Url + "holder");
        var waitForHolder = Stopwatch.StartNew();
        while (waitForHolder.Elapsed < TimeSpan.FromSeconds(5) && server.Received.Count == 0)
        {
            await Task.Delay(5);
        }

        Assert.Single(server.Received);

        // 槽位一直不放：只有「预算封顶排队」才能让这条请求在 150ms 左右失败。
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "queued", c => c.Deadline = TimeSpan.FromMilliseconds(150))
                .WaitAsync(TimeSpan.FromSeconds(3)));
        watch.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Contains("deadline of", ex.Message, StringComparison.Ordinal);
        // 上限放到 1 秒：真正的判据是「槽位一直不放也没等到」——回归时会撞下面的 3 秒兜底而失败，
        // 这里只是把「被预算截断」和「等到槽位才失败」区分开，不该被 CI 抖动打成假失败。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"排队没有被总预算截断，实际等了 {watch.Elapsed}");

        // 没有换成 ERR_QUEUE_TIMEOUT：报错要指向真正的约束（预算），不是那个没设过的旋钮。
        Assert.Single(server.Received);

        gate.SetResult();
        await holder;
    }

    // ------------------------------------------------------------------
    // 与 Timeout 的关系 / 合并 / 校验
    // ------------------------------------------------------------------
    [Fact]
    public async Task Deadline_InheritedFromDefaults_ButPerCallValueWins()
    {
        await using var server = TestHttpServer.Start(async request =>
        {
            await Task.Delay(300);
            return TestJson.Response(new { path = request.Path });
        });

        // 实例默认 100ms：任何请求都撑不过 300ms 的下游
        using var client = new AxiosClient(new AxiosConfig { Deadline = TimeSpan.FromMilliseconds(100) });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "default"));
        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);

        // 按请求覆盖成更宽的值：实例默认必须让位
        var response = await client.GetAsync(server.Url + "override", c => c.Deadline = TimeSpan.FromSeconds(10));
        Assert.Equal(200, response.Status);

        // 显式 null = 这一次不要总预算（与「没设置」是两种语义）
        var withoutDeadline = await client.GetAsync(server.Url + "none", c => c.Deadline = null);
        Assert.Equal(200, withoutDeadline.Status);
    }

    [Fact]
    public async Task ZeroDeadline_MeansNoBudget()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(200);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();

        // 0 与 null 同义（与 Timeout = 0 的取值口径一致）：不该被当成「立刻超时」
        var response = await client.GetAsync(server.Url + "zero", c => c.Deadline = TimeSpan.Zero);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Deadline_ClampsRetryBackoffToRemainingBudget()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient();

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "boom", c =>
        {
            // 退避 5 秒，总预算只有 200ms：睡过截止点等于把 Deadline 变成摆设。
            c.Retry.Policy = new RetryPolicy(5, TimeSpan.FromSeconds(5));
            c.Deadline = TimeSpan.FromMilliseconds(200);
        }));
        watch.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"实际耗时 {watch.Elapsed}：退避没有被削到剩余预算");
    }

    [Fact]
    public async Task Deadline_HonoursCustomTimeoutMessage()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "slow", c =>
        {
            c.Deadline = TimeSpan.FromMilliseconds(100);
            c.Callbacks.TimeoutMessage = "调用方约定的超时文案";
        }));

        // 自定义文案是调用方的显式措辞：超时与总预算耗尽都用它，不再拼接库自己的说明。
        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Equal("调用方约定的超时文案", ex.Message);
    }

    [Fact]
    public async Task HugeDeadline_IsClampedAndDoesNotOverflow()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 超长预算等价于「不设」：换算成时间戳时按计时器上限收敛，不能溢出成负数（那会变成「立刻过期」）。
        var response = await client.GetAsync(server.Url + "huge", c => c.Deadline = TimeSpan.MaxValue);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task NegativeDeadline_ThrowsBadOptionValue()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/x", c => c.Deadline = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task Deadline_DoesNotLeakIntoNextRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 一次带总预算的请求不能把截止点留在实例默认里：下一条请求应当是「没有总预算」的。
        var first = await client.GetAsync(server.Url + "one", c => c.Deadline = TimeSpan.FromSeconds(5));
        Assert.Equal(200, first.Status);

        var second = await client.GetAsync(server.Url + "two");
        Assert.Equal(200, second.Status);
        Assert.Null(client.Defaults.Deadline);
    }
}
