
using AxiosNet.Circuit;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 熔断：Closed/Open/HalfOpen 状态机、按 host 计数、策略自定义、淘汰上限。
/// </summary>
public sealed class CircuitBreakerTests
{
    // ------------------------------------------------------------------
    // 打开 / 拒绝
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConsecutiveFailures_OpenCircuit_ThenRejectWithoutSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var breaker = new CircuitBreaker { FailureThreshold = 2, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        // 前两次真的发出去、真的失败（阈值 2 才打开）
        for (var i = 0; i < 2; i++)
        {
            var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
            Assert.Equal(AxiosException.ErrBadResponse, failed.Code);
        }

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));

        // 第三次：请求根本不会发出（连接都不建）
        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Equal(2, server.Received.Count);
        Assert.NotNull(rejected.RetryAfter);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
        Assert.Contains(HostOf(server), rejected.Message);
    }

    [Fact]
    public async Task ClientErrors_AreNotCountedAsFailures()
    {
        // 4xx 是「这次请求有问题」，不是「下游挂了」：否则一个写错的接口会把整条链路熔断。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 404));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        for (var i = 0; i < 4; i++)
        {
            var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
            Assert.Equal(AxiosException.ErrBadRequest, failed.Code);
        }

        // 阈值 1 也没打开：一次都不计数
        Assert.Equal(4, server.Received.Count);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState(HostOf(server)));
    }

    [Fact]
    public async Task Timeout_CountsAsDownstreamFailure()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(500);
            return TestJson.Response(new { ok = true });
        });

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        // 超时（ECONNABORTED）与网络错误同级：下游不响应同样是「不可用」
        var timedOut = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "x", c => c.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosException.ErrConnAborted, timedOut.Code);
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));

        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
    }

    [Fact]
    public async Task Cancellation_DoesNotTripCircuit()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(300);
            return TestJson.Response(new { ok = true });
        });

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var canceled = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x", c =>
        {
            c.CancellationToken = cts.Token;
            c.Timeout = TimeSpan.FromSeconds(5);
        }));

        Assert.Equal(AxiosException.ErrCanceled, canceled.Code);

        // 调用方主动取消不是下游故障：阈值 1 也没打开（这条 host 确实被计数过，只是这一笔不算）
        Assert.Equal(1, breaker.TrackedHostCount);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState(HostOf(server)));
    }

    [Fact]
    public void Context_ClassifiesTransportFailures_ButNotCancellation()
    {
        var uri = new Uri("https://api.example.com/orders");

        // 成功（没有异常）
        Assert.False(new AxiosCircuitContext("h", uri).IsFailure);
        Assert.True(new AxiosCircuitContext("h", uri).IsSuccess);

        // 取消不算下游故障
        Assert.False(new AxiosCircuitContext("h", uri, null, new OperationCanceledException()).IsFailure);

        // 网络层失败算
        Assert.True(new AxiosCircuitContext("h", uri, null, new HttpRequestException("dns failure")).IsFailure);
        Assert.True(new AxiosCircuitContext("h", uri, null, new IOException("connection reset")).IsFailure);

        // validateStatus 放行的 5xx 没有异常，但状态码仍要落账：算失败、不算成功
        var accepted5xx = new AxiosCircuitContext("h", uri, statusCode: 500);
        Assert.Equal(500, accepted5xx.StatusCode);
        Assert.False(accepted5xx.IsSuccess);
        Assert.True(accepted5xx.IsFailure);

        // 放行的 4xx：既不算失败，也不再算成功（与抛异常那条路一致：不清零失败计数）
        var accepted4xx = new AxiosCircuitContext("h", uri, statusCode: 404);
        Assert.False(accepted4xx.IsSuccess);
        Assert.False(accepted4xx.IsFailure);
    }

    [Fact]
    public async Task ValidateStatusAccepted5xx_StillOpensCircuit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        // 冷却取长值：本用例只断言「记账 + 拒绝」，不需要冷却结束。
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = LongBreak };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        // validateStatus 放行 5xx：请求本身不抛错，但熔断必须照样记账
        var response = await client.GetAsync(server.Url + "x", c => c.Callbacks.ValidateStatus = _ => true);
        Assert.Equal(500, response.Status);
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));

        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
    }

    // ------------------------------------------------------------------
    // 半开探测与恢复
    // ------------------------------------------------------------------

    [Fact]
    public async Task HalfOpen_ProbeSucceeds_ClosesCircuit()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        // 先用长冷却把「冷却期内拒绝」钉死，再压回短冷却等它过期。
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = LongBreak };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));

        // 冷却期内：拒绝，不发
        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Single(server.Received);

        // 压回短冷却：验证的仍是「时间真的过去了才放行」，只省掉 30 秒等待。
        breaker.BreakDuration = ShortBreak;
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // 冷却结束：这一次是探测请求，成功即恢复
        var probe = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, probe.Status);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState(HostOf(server)));

        // 恢复后照常放行
        var after = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, after.Status);
        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task HalfOpen_ProbeFails_ReopensImmediately()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        // 阈值取 5：如果探测失败只是「再记一笔」，熔断器会回到闭合态等 5 次失败 —— 那是错的。
        var breaker = new CircuitBreaker { FailureThreshold = 5, BreakDuration = ShortBreak };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        // 攒够 5 次失败才打开
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        }

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // 探测失败 → 立刻重新打开（不回到闭合态）
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));
        Assert.Equal(6, server.Received.Count);

        // 重开后拉长冷却：断言「不发」不能被冷却过期打断。
        breaker.BreakDuration = LongBreak;

        // 重新打开后又是「不发」
        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Equal(6, server.Received.Count);
    }

    [Fact]
    public async Task HalfOpen_IgnoredResult_ReleasesProbeSlot()
    {
        // 探测拿到 404（不计失败也不计成功）：熔断器仍停半开，但探测名额必须归还，
        // 否则一次「被忽略」的探测会把熔断器永久卡在半开，再也恢复不了。
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) switch
        {
            1 => TestJson.Response(new { error = "boom" }, statusCode: 500),
            2 => TestJson.Response(new { error = "nope" }, statusCode: 404),
            _ => TestJson.Response(new { ok = true })
        });

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = ShortBreak };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var probe = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosException.ErrBadRequest, probe.Code);
        Assert.Equal(AxiosCircuitState.HalfOpen, breaker.GetState(HostOf(server)));

        // 名额已归还：下一次请求还能探测（否则这里是 ERR_CIRCUIT_OPEN）
        var recovered = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, recovered.Status);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState(HostOf(server)));
    }

    [Fact]
    public async Task StateChanged_ReportsOpenProbeAndRecovery()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = ShortBreak };
        var transitions = new List<AxiosCircuitTransition>();
        breaker.StateChanged += transitions.Add;

        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await client.GetAsync(server.Url + "x");

        Assert.Contains(transitions, t => t.Previous == AxiosCircuitState.Closed && t.Current == AxiosCircuitState.Open);
        Assert.Contains(transitions, t => t.Previous == AxiosCircuitState.Open && t.Current == AxiosCircuitState.HalfOpen);
        Assert.Contains(transitions, t => t.Previous == AxiosCircuitState.HalfOpen && t.Current == AxiosCircuitState.Closed);
    }

    // ------------------------------------------------------------------
    // 维度隔离与自定义维度
    // ------------------------------------------------------------------

    [Fact]
    public async Task Circuit_IsPerHost_NotGlobal()
    {
        await using var broken = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        await using var healthy = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(broken.Url + "x"));

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(broken)));
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState(HostOf(healthy)));

        // 另一个 host 完全不受影响
        var response = await client.GetAsync(healthy.Url + "x");
        Assert.Equal(200, response.Status);
        Assert.Single(healthy.Received);
    }

    [Fact]
    public async Task KeySelector_CanMergePortsIntoOneCircuit()
    {
        await using var broken = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        await using var healthy = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var breaker = new CircuitBreaker
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),

            // 按 host 合并端口：同一台机器上的两个服务共用一个熔断判定
            KeySelector = ctx => ctx.Uri.Host
        };

        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(broken.Url + "x"));

        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(healthy.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Empty(healthy.Received);
    }

    [Fact]
    public async Task TrackedHosts_StayBounded()
    {
        await using var a = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var b = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var c = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var breaker = new CircuitBreaker { MaxTrackedHosts = 2 };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await client.GetAsync(a.Url + "x");
        await client.GetAsync(b.Url + "x");
        await client.GetAsync(c.Url + "x");

        // 面向任意 host（多租户 / 动态域名）时状态表不能无限长：满员后淘汰「无账可记」的条目
        Assert.True(breaker.TrackedHostCount <= 2, $"TrackedHostCount = {breaker.TrackedHostCount}");
    }

    [Fact]
    public async Task OpenCircuitEntry_IsNotEvictedUnderPressure()
    {
        await using var broken = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        await using var healthy = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // 上限压到 1：新 host 一定触发淘汰
        var breaker = new CircuitBreaker
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            MaxTrackedHosts = 1
        };

        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(broken.Url + "x"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(broken)));

        // 新 host 进来：可淘汰的只有「无账可记」的条目，正在熔断的那条不能丢 ——
        // 丢了等于让刚判定为不可用的下游悄悄恢复放行。
        await client.GetAsync(healthy.Url + "x");

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(broken)));

        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(broken.Url + "x"));
        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Single(broken.Received);
    }

    [Fact]
    public void OpenCircuitEntry_SurvivesRepeatedEvictionPressure()
    {
        var breaker = new CircuitBreaker
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            MaxTrackedHosts = 1
        };

        var uri = new Uri("https://down.example.com/x");
        var open = new AxiosCircuitContext("down", uri);

        Assert.True(breaker.ShouldAllow(open));
        breaker.Record(new AxiosCircuitContext("down", uri, statusCode: 500));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState("down"));

        // 反复制造淘汰压力（每次新 host 都会触发一次 Evict）：已处于 Open 的条目不会被选中。
        // 「快照之后才转入 Open」的交错由 CandidateChangedDuringEviction_IsRecheckedUnderGate 覆盖。
        for (var i = 0; i < 50; i++)
        {
            breaker.ShouldAllow(new AxiosCircuitContext("new-" + i, uri));
            Assert.Equal(AxiosCircuitState.Open, breaker.GetState("down"));
            Assert.False(breaker.ShouldAllow(open));
        }
    }

    [Fact]
    public void CandidateChangedDuringEviction_IsRecheckedUnderGate()
    {
        var breaker = new CircuitBreaker
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            MaxTrackedHosts = 1
        };

        var uri = new Uri("https://down.example.com/x");
        var down = new AxiosCircuitContext("down", uri);

        Assert.True(breaker.ShouldAllow(down));

        // 候选是在无锁快照里选出的：这里在「选中之后、删除之前」把 down 推进成 Open，
        // 模拟并发请求恰好在这个窗口里回报失败。复核若不在该条目的 Gate 内做，down 会被
        // 当成闭合态误删 —— 下次请求重建状态并直接放行一个已知不可用的下游。
        breaker.EvictionCandidateChosen = key =>
        {
            if (key == "down")
            {
                breaker.Record(new AxiosCircuitContext("down", uri, statusCode: 500));
            }
        };

        // 新 host 触发 Evict；上限是软上限：没有可删的条目时宁可超限，也不丢正在熔断的账。
        breaker.ShouldAllow(new AxiosCircuitContext("new-0", uri));

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState("down"));
        Assert.False(breaker.ShouldAllow(down));
    }

    [Fact]
    public async Task CustomPolicy_DrivesTheGateOncePerAttempt()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        var policy = new CountingPolicy();
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = policy } });

        // 失败一次：放行 1 次、回报 1 次、算 1 次下游故障
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal((1, 1, 1), (policy.Allowed, policy.Recorded, policy.Failures));

        // 成功一次：放行 1 次、回报 1 次、故障数不变（契约：每次放行恰好回报一次）
        await client.GetAsync(server.Url + "x");
        Assert.Equal((2, 2, 1), (policy.Allowed, policy.Recorded, policy.Failures));
    }

    [Fact]
    public async Task CustomPolicy_DenyShortCircuitsWithoutSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = new DenyAllPolicy(TimeSpan.FromSeconds(7)) } });

        var rejected = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrCircuitOpen, rejected.Code);
        Assert.Equal(TimeSpan.FromSeconds(7), rejected.RetryAfter);
        Assert.Contains("7s", rejected.Message);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 与重试 / 配置合并的配合
    // ------------------------------------------------------------------

    [Fact]
    public async Task RetryStopsWhenCircuitOpens()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig
        {
            // 允许 5 次重试、零等待：如果没有熔断闸门，服务端会收到 6 个请求
            Retry = { Policy = new RetryPolicy(5, TimeSpan.Zero) },
            Circuit = { Policy = breaker }
        });

        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 第一次 5xx 就把熔断打开了，第二次尝试被闸门拦下：服务端只收到 1 个请求
        Assert.Equal(AxiosException.ErrCircuitOpen, failed.Code);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestPolicyNull_DisablesInstanceCircuit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState(HostOf(server)));

        // 显式 null = 本次请求绕过熔断（探针 / 健康检查这类请求不该被挡住）
        var bypassed = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "x", c => c.Circuit.Policy = null));

        Assert.Equal(AxiosException.ErrBadResponse, bypassed.Code);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task OnRejected_IsInvokedWithHostAndWait()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        var rejected = new List<AxiosCircuitContext>();

        using var client = new AxiosClient(new AxiosConfig
        {
            Circuit = { Policy = breaker, OnRejected = rejected.Add }
        });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        var only = Assert.Single(rejected);
        Assert.Equal(HostOf(server), only.Host);

        // 回调里就能拿到建议等待，不必再去 catch 异常
        Assert.True(only.RetryAfter > TimeSpan.Zero);
        Assert.True(only.RetryAfter <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task InvalidSettings_AreRejectedBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var breaker = new CircuitBreaker { FailureThreshold = 0 };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, failed.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void Reset_ClosesCircuitImmediately()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromHours(1) };
        var uri = new Uri("https://api.example.com/orders");
        var context = new AxiosCircuitContext("api.example.com", uri);

        var transitions = new List<AxiosCircuitTransition>();
        breaker.StateChanged += transitions.Add;

        Assert.True(breaker.ShouldAllow(context));
        breaker.Record(new AxiosCircuitContext("api.example.com", uri, null, new HttpRequestException("down")));

        Assert.Equal(AxiosCircuitState.Open, breaker.GetState("api.example.com"));
        Assert.False(breaker.ShouldAllow(context));

        Assert.True(breaker.Reset("api.example.com"));
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState("api.example.com"));
        Assert.Equal(0, breaker.TrackedHostCount);

        // 手工恢复也是「状态变了」：订阅方（告警 / 看板）要能看到这次 Open -> Closed
        Assert.Contains(transitions, t => t.Previous == AxiosCircuitState.Open && t.Current == AxiosCircuitState.Closed);

        // 重置后立刻放行（下一次失败重新从 0 开始计数）
        Assert.True(breaker.ShouldAllow(context));
    }

    [Fact]
    public void ResetAll_ClearsEveryDimension_ButOnlyNotifiesNonClosedOnes()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromHours(1) };
        var transitions = new List<AxiosCircuitTransition>();
        breaker.StateChanged += transitions.Add;

        // 一个维度打开、一个维度只放过行（仍 Closed）
        Assert.True(breaker.ShouldAllow(ContextFor("down.example.com")));
        breaker.Record(FailedContext("down.example.com"));
        Assert.True(breaker.ShouldAllow(ContextFor("up.example.com")));
        Assert.Equal(2, breaker.TrackedHostCount);

        var beforeReset = transitions.Count;
        breaker.Reset();

        Assert.Equal(0, breaker.TrackedHostCount);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState("down.example.com"));

        // 只有「本来就非 Closed」的维度在 Reset 时产生迁移事件
        var afterReset = transitions.Skip(beforeReset).ToArray();
        Assert.Single(afterReset);
        Assert.Equal("down.example.com", afterReset[0].Host);
        Assert.Equal(AxiosCircuitState.Open, afterReset[0].Previous);
    }

    [Fact]
    public void ResetSingleHost_UnknownOrNullHost_ReturnsFalse()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromHours(1) };

        Assert.False(breaker.Reset("never-seen.example.com"));
        Assert.False(breaker.Reset(null!));
    }

    [Fact]
    public void GetRetryAfter_UntrackedHost_AndHalfOpen_AreZero()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = ShortBreak };

        // 从未失败过：没有可回报的等待时间
        Assert.Equal(TimeSpan.Zero, breaker.GetRetryAfter(ContextFor("fresh.example.com")));

        Assert.True(breaker.ShouldAllow(ContextFor("breaker.example.com")));
        breaker.Record(FailedContext("breaker.example.com"));
        var openWait = breaker.GetRetryAfter(ContextFor("breaker.example.com"));
        Assert.True(openWait > TimeSpan.Zero);

        // 冷却结束后进入半开：名额随时可能被前一个探测归还，不再给具体时刻
        Thread.Sleep((int)ShortBreak.TotalMilliseconds + 100);
        Assert.True(breaker.ShouldAllow(ContextFor("breaker.example.com")));
        Assert.Equal(AxiosCircuitState.HalfOpen, breaker.GetState("breaker.example.com"));
        Assert.Equal(TimeSpan.Zero, breaker.GetRetryAfter(ContextFor("breaker.example.com")));
    }

    [Fact]
    public void Record_WithoutPriorGateCheck_IsIgnored()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromHours(1) };

        // 没有经过 ShouldAllow 的账簿没有可记的账，更不能凭空建出一条维度
        breaker.Record(FailedContext("unknown.example.com"));

        Assert.Equal(0, breaker.TrackedHostCount);
        Assert.Equal(AxiosCircuitState.Closed, breaker.GetState("unknown.example.com"));
    }

    [Fact]
    public void StateChangedHandler_Throwing_DoesNotChangeTheDecision()
    {
        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromHours(1) };
        breaker.StateChanged += _ => throw new InvalidOperationException("observer down");

        // 观测代码抛异常不该把请求结果带偏
        Assert.True(breaker.ShouldAllow(ContextFor("api.example.com")));
        breaker.Record(FailedContext("api.example.com"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState("api.example.com"));
        Assert.False(breaker.ShouldAllow(ContextFor("api.example.com")));
    }

    private static AxiosCircuitContext ContextFor(string host)
        => new(host, new Uri("https://" + host + "/orders"));

    private static AxiosCircuitContext FailedContext(string host)
        => new(host, new Uri("https://" + host + "/orders"), null, new HttpRequestException("down"));

    /// <summary>熔断的计数维度：默认 <c>host:port</c>。</summary>
    private static string HostOf(TestHttpServer server) => $"127.0.0.1:{server.Port}";

    /// <summary>
    /// 「短冷却」：只用于「等它过期」的断言 —— 冷却结束后发探测请求，等够 500ms 即可。
    /// 「冷却期内仍拒绝」的断言必须配 <see cref="LongBreak"/>，否则就是测试和调度延迟赛跑。
    /// </summary>
    private static readonly TimeSpan ShortBreak = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 「长冷却」：用于断言「刚打开 → 紧接着的请求一定被拒」。30 秒远大于任何调度抖动，
    /// 且这些用例从不真的等它过期。
    /// </summary>
    private static readonly TimeSpan LongBreak = TimeSpan.FromSeconds(30);

    /// <summary>记录调用次数的策略：用于锁定「每次放行恰好回报一次」的管线契约。</summary>
    private sealed class CountingPolicy : IAxiosCircuitPolicy
    {
        public int Allowed;

        public int Recorded;

        public int Failures;

        public bool ShouldAllow(AxiosCircuitContext context)
        {
            Allowed++;
            return true;
        }

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.Zero;

        public void Record(AxiosCircuitContext context)
        {
            Recorded++;
            if (context.IsFailure)
            {
                Failures++;
            }
        }
    }

    /// <summary>一律拒绝的策略：验证自定义实现能接管闸门，且拒绝时请求不会发出。</summary>
    private sealed class DenyAllPolicy(TimeSpan retryAfter) : IAxiosCircuitPolicy
    {
        public bool ShouldAllow(AxiosCircuitContext context) => false;

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => retryAfter;

        public void Record(AxiosCircuitContext context)
        {
        }
    }
}