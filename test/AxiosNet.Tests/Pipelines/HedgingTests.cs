using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Circuit;
using AxiosNet.Retry;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Resilience;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 并发对冲：首发之后按延迟放出副本，第一个成功结果胜出，其余分支取消并收尾；
/// 所有分支都失败时交给重试。不满足对冲条件的请求（流式、非幂等、正文不可并发重放、响应侧带共享状态）
/// 按单次发送处理。
/// <para>
/// 整个类进 <see cref="TimingCollection"/>：错峰用例断言的是「分支按 200ms 间隔起跑」，
/// 并行集合抢 CPU 时到达时刻会被挤到 60ms 量级（假失败）。这个类本来就只有 3 秒，
/// 串行化的代价小于一条永远在抖的护栏。
/// </para>
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class HedgingTests
{
    private static AxiosConfig.HedgingSettings Hedge(int maxHedges, TimeSpan? delay = null)
        => new() { MaxHedges = maxHedges, Delay = delay ?? TimeSpan.FromMilliseconds(50) };

    [Fact]
    public async Task Hedging_NotConfigured_SendsOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hedging_StartsWithDelayedCopy_AndTheFirstSuccessWins()
    {
        var arrivals = 0;

        // 第一条被收到的分支压到 5s 才回（它已经输了），第二条立刻回；胜出者必须是先成功的那条，
        // 而不是先发出的那条。5s 的延迟只在断言之后作为后台任务收尾，不参与等待。
        await using var server = TestHttpServer.Start(async _ =>
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return TestJson.Response(new { branch = 1 });
            }

            return TestJson.Response(new { branch = 2 });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1, delay: TimeSpan.FromMilliseconds(200))
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("x");
        stopwatch.Stop();

        Assert.Equal(200, response.Status);
        Assert.Equal(2, Assert.IsType<JsonElement>(response.Data).GetProperty("branch").GetInt32());
        Assert.Equal(2, server.Received.Count);

        // 胜出者出现后不再等败者：5s 的慢分支不能把调用方拖住。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"对冲胜出后仍等待败者：{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Hedging_FailedBranch_DoesNotFailTheRequest_WhenTheFirstBranchSucceeds()
    {
        var arrivals = 0;

        // 先到的分支 600ms 后成功；后起跑的分支立刻 503。败者失败不能盖掉首发的结果。
        await using var server = TestHttpServer.Start(async _ =>
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600));
                return TestJson.Response(new { branch = 1 });
            }

            return new TestResponse(503, Body: Array.Empty<byte>());
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1)
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Equal(1, Assert.IsType<JsonElement>(response.Data).GetProperty("branch").GetInt32());
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_AllBranchesFail_WithoutRetry_ThrowsTheFailure()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1)
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_AllBranchesFail_IsHandedToRetry()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = new() { Policy = RetryPolicy.Constant(1, TimeSpan.Zero) },
            Hedging = Hedge(maxHedges: 1)
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 每次尝试 = 一组对冲分支（2 条），重试 1 次 → 4 条请求；全失败后错误交给重试再抛给调用方。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(4, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_SkipsNonIdempotentMethods_ByDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1, delay: TimeSpan.Zero)
        });

        var response = await client.PostAsync("x", new { name = "a" });

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hedging_HedgesNonIdempotentMethods_WhenExplicitlyAllowed()
    {
        var arrivals = 0;

        // 第一条分支压 300ms：保证后起跑的分支真的被发出去（否则可能在起跑前就被胜出者取消）。
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = new()
            {
                MaxHedges = 1,
                Delay = TimeSpan.Zero,
                HedgeNonIdempotentMethods = true
            }
        });

        var response = await client.PostAsync("x", new { name = "a" });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, request => Assert.Equal("POST", request.Method));
    }

    [Fact]
    public async Task Hedging_PerRequestOverride_EnablesHedgingForOneCall()
    {
        var arrivals = 0;

        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 实例上没配对冲，只有这一次请求要：按请求项必须真的接进管线。
        var response = await client.GetAsync("x", c => c.Hedging.MaxHedges = 1);

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_RejectsNegativeMaxHedges()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Hedging = new() { MaxHedges = -1 }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
    }

    [Fact]
    public async Task Hedging_RejectsTooManyBranches()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Hedging = new() { MaxHedges = AxiosConfig.HedgingSettings.MaxHedgesLimit + 1 }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
    }

    [Fact]
    public async Task Hedging_RejectsNegativeDelay()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.FromMilliseconds(-1) }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
    }

    [Fact]
    public async Task Hedging_RejectsDelayTimesMaxHedgesBeyondTimerRange()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",

            // 每条分支都单独在范围之内，乘上分支序号就越界：第 16 条分支的起跑时刻是 160 天，
            // 会被计时器上限收敛成同一个时刻 —— 那等于这条分支永不发出，必须提前报错而不是静默不发。
            Hedging = new() { MaxHedges = 16, Delay = TimeSpan.FromDays(10) }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
    }

    [Fact]
    public async Task Hedging_SaturatedGate_ReturnsAsSoonAsAWinnerAppears()
    {
        var arrivals = 0;

        // 首发占住唯一的槽位并慢慢回；对冲分支只能排在闸门里等它让位。
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Limits = { MaxConcurrentRequests = 1, QueueTimeout = TimeSpan.FromSeconds(5) },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("x");
        stopwatch.Stop();

        Assert.Equal(200, response.Status);

        // 败者在闸门里排队时也不能拖住调用方：收尾必须被分支令牌打断，而不是等满 5s 的 QueueTimeout。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"胜出后仍等败者排队：{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Hedging_PrefersTheRealFailureOverQueueTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = TestHttpServer.Start(async request =>
        {
            if (request.Path == "/hold")
            {
                await release.Task;
                return TestJson.Response(new { ok = true });
            }

            // 首发 200ms 后才 503；另一条分支会先一步在闸门里排队失败（QueueTimeout = Zero）。
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            return new TestResponse(503, Body: Array.Empty<byte>());
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Limits = { MaxConcurrentRequests = 2, QueueTimeout = TimeSpan.Zero },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        // 先用一条长请求占住一个槽位（它自己不对冲，否则两条分支会把两个槽位都占掉）。
        var occupying = client.GetAsync("hold", c => c.Hedging.MaxHedges = 0);
        await WaitUntilAsync(() => server.Received.Count >= 1);

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 先失败的是「没抢到槽位」，但它说明不了下游，而且库级不重试它：
        // 抛给调用方的必须是首发那个可重试的 503。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(503, exception.Status);

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task Hedging_PrefersTheRealFailureOverQueueFull()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = TestHttpServer.Start(async request =>
        {
            if (request.Path == "/hold")
            {
                await release.Task;
                return TestJson.Response(new { ok = true });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
            return new TestResponse(503, Body: Array.Empty<byte>());
        });

        // 占位 1 个槽位后只剩 1 个在途名额 + 1 个排队位：三条分支里必有一条撞上队列上限。
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Limits = { MaxConcurrentRequests = 2, MaxQueueLength = 1, QueueTimeout = TimeSpan.FromSeconds(5) },
            Hedging = new() { MaxHedges = 2, Delay = TimeSpan.Zero }
        });

        var occupying = client.GetAsync("hold", c => c.Hedging.MaxHedges = 0);
        await WaitUntilAsync(() => server.Received.Count >= 1);

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 先失败的可能是「队列满」，但它说明不了下游；抛给调用方的必须是那个 503。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(503, exception.Status);

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task Hedging_DiscardedBranches_ReleaseTheirLeaseAndSlot()
    {
        var arrivals = 0;

        // 首发 200ms 后才回，对冲分支立刻成功：胜出者是后发的那条，首发成了被取消的败者。
        // （「败者已经成功返回」那种竞态写不出稳定用例，见 Attempt.Hedging 里 Discard 的注释。）
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Limits = { MaxConcurrentRequests = 2, QueueTimeout = TimeSpan.Zero },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);

        // 败者持有的连接租约与并发槽位都已归还。
        Assert.Equal(0, client.Pool!.GetStatistics().Endpoints.Sum(endpoint => endpoint.ActiveLeases));

        // 两个槽位都空着：QueueTimeout = Zero 下连续两次普通请求都得拿到槽位（漏一个就会 ERR_QUEUE_TIMEOUT）。
        await client.GetAsync("x", c => c.Hedging.MaxHedges = 0);
        await client.GetAsync("x", c => c.Hedging.MaxHedges = 0);
    }

    [Fact]
    public async Task Hedging_AppliesToTypedCalls()
    {
        var arrivals = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return TestJson.Response(new { branch = 7 });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1, delay: TimeSpan.Zero)
        });

        // 泛型入口会预造响应对象：两条分支不能共用一个实例，但也不该因此放弃对冲。
        var response = await client.GetAsync<BranchPayload>("x");

        Assert.Equal(7, Assert.IsType<BranchPayload>(response.Data).Branch);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_EachBranchKeepsItsOwnResponseContext()
    {
        var arrivals = 0;

        // 先到的分支：响应头立刻到、正文 300ms 后到 —— 它在 300ms 时反序列化并胜出。
        // 后到的分支：响应头晚 100ms 到（它的响应上下文写在后）、正文再晚 400ms —— 它必然落败，
        // 但它的写入落在胜者反序列化之前，共享同一份 config 时胜者会读到它的响应头。
        await using var server = TestHttpServer.Start(async _ =>
        {
            var branch = Interlocked.Increment(ref arrivals);
            if (branch == 1)
            {
                return TestJson.Response(new { branch }) with
                {
                    Headers = BranchHeader(branch),
                    BodyDelay = TimeSpan.FromMilliseconds(300)
                };
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            return TestJson.Response(new { branch }) with
            {
                Headers = BranchHeader(branch),
                BodyDelay = TimeSpan.FromMilliseconds(400)
            };
        });

        var serializer = new ResponseContextSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = { ContentSerializer = serializer },
            Hedging = Hedge(maxHedges: 1, delay: TimeSpan.Zero)
        });

        var response = await client.GetAsync<BranchPayload>("x");

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);

        // 胜出者是先到的分支：它反序列化时读到的必须是自己的响应头，而不是另一条分支刚写进去的那份。
        Assert.Equal(1, Assert.IsType<BranchPayload>(response.Data).Branch);

        var observed = Assert.Single(serializer.Observed);
        Assert.True(observed.Matched, $"分支 {observed.Branch} 读到了别人的响应头 {observed.Header}");

        // 胜出者的响应上下文回填到共享配置：响应拦截器与调用方仍能从这里读本次响应。
        Assert.Equal(response.Status, response.Config?.Response?.Status);
        Assert.Equal(observed.Header, response.Config?.Response?.GetHeader("X-Branch"));
    }

    [Fact]
    public async Task Hedging_CircuitBreaker_CountsOncePerAttempt()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Circuit = { Policy = new CircuitBreaker(failureThreshold: 2, breakDuration: TimeSpan.FromMinutes(1)) },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        // 熔断按「一次逻辑尝试一笔」记账：前两次各放出 2 条分支，2 笔失败后打开。
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));
        Assert.Equal(4, server.Received.Count);

        // 第三次被熔断拦下，一条分支都不发（按分支记账的话第二次就已经被拒了）。
        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrCircuitOpen, exception.Code);
        Assert.Equal(4, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_StaggersBranches_ByIndexTimesDelay()
    {
        var arrivals = new ConcurrentQueue<TimeSpan>();
        var clock = Stopwatch.StartNew();

        // 每条分支都要 600ms 才回：第 2、3 条分别在 200ms / 400ms 起跑，首发 600ms 时胜出。
        await using var server = TestHttpServer.Start(async _ =>
        {
            arrivals.Enqueue(clock.Elapsed);
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = new() { MaxHedges = 2, Delay = TimeSpan.FromMilliseconds(200) }
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);

        var stamps = arrivals.OrderBy(stamp => stamp).ToArray();
        Assert.Equal(3, stamps.Length);
        Assert.True(
            stamps[1] - stamps[0] >= TimeSpan.FromMilliseconds(120),
            $"第 2 条分支起跑过早：{(stamps[1] - stamps[0]).TotalMilliseconds:0}ms");
        Assert.True(
            stamps[2] - stamps[1] >= TimeSpan.FromMilliseconds(120),
            $"第 3 条分支起跑过早：{(stamps[2] - stamps[1]).TotalMilliseconds:0}ms");
    }

    [Fact]
    public async Task Hedging_SkipsResponsesWithSharedDownloadState()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Callbacks = { OnDownloadProgress = _ => { } },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        // 下载进度回调按「一条流」设计：两条分支会各报一份（进度翻倍），按单次发送处理。
        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hedging_SkipsStreamResponses()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = Hedge(maxHedges: 1, delay: TimeSpan.Zero)
        });

        var response = await client.GetAsync("x", c => c.ResponseType = ResponseType.Stream);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);

        await using var body = Assert.IsAssignableFrom<Stream>(response.Data);
    }

    [Fact]
    public async Task Hedging_SkipsBodyThatCannotBeSerializedConcurrently()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = new()
            {
                MaxHedges = 1,
                Delay = TimeSpan.Zero,
                HedgeNonIdempotentMethods = true
            }
        });

        // 网络流形态的正文只有一个读指针：两条分支并发序列化会互相踩，按单次发送处理。
        await using var payload = new NonSeekableStream(Encoding.UTF8.GetBytes("{\"name\":\"a\"}"));
        var response = await client.PostAsync("x", payload);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    private static Dictionary<string, string> BranchHeader(int branch)
        => new() { ["X-Branch"] = branch.ToString(CultureInfo.InvariantCulture) };

    /// <summary>等测试服务器真的收到请求（异步启动的并发请求无法靠固定等待断言）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待测试服务器收到请求超时。");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>对冲用例的响应模型：分支号由服务端按到达顺序给。</summary>
    private sealed class BranchPayload
    {
        public int Branch { get; set; }
    }

    /// <summary>
    /// 反序列化时读 <see cref="AxiosConfig.Response"/> 的响应头（文档允许的用法），
    /// 记下「正文里的分支号」与「读到的分支头」是否一致 —— 对冲各分支的响应上下文互不覆盖时必然一致。
    /// </summary>
    private sealed class ResponseContextSerializer : IContentSerializer
    {
        private readonly ConcurrentQueue<(int Branch, string? Header, bool Matched)> _observed = new();

        public IReadOnlyCollection<(int Branch, string? Header, bool Matched)> Observed => _observed;

        public string Serialize(object? value, Type type, AxiosConfig config) => JsonSerializer.Serialize(value, type);

        public object? Deserialize(string text, Type type, AxiosConfig config)
        {
            using var document = JsonDocument.Parse(text);
            var branch = document.RootElement.GetProperty("branch").GetInt32();
            var header = config.Response?.GetHeader("X-Branch");

            _observed.Enqueue((branch, header, header == branch.ToString(CultureInfo.InvariantCulture)));
            return new BranchPayload { Branch = branch };
        }
    }
}
