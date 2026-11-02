using System.Diagnostics;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 第三方重试策略（<see cref="IAxiosRetryPolicy"/>）的契约：首次失败就咨询、次数上限由库先挡、
/// 延迟被尊重、拒绝时原样抛出、库级终止原因不咨询策略，以及与 failover / hedging 的组合边界。
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class RetryPolicyContractTests
{
    [Fact]
    public async Task FirstFailure_IsReportedAsAttemptOne_AndAsksThePolicy()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var policy = new RecordingPolicy { MaxRetries = 1 };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(1, policy.ShouldRetryCalls);
        Assert.Equal(1, policy.GetDelayCalls);

        var context = Assert.Single(policy.Contexts);
        Assert.Equal(1, context.Attempt);
        Assert.Equal(1, context.MaxRetries);
        Assert.Equal(500, context.StatusCode);
        Assert.Equal(AxiosException.ErrBadResponse, context.ErrorCode);
        Assert.Equal(HttpMethod.Get, context.Method);
        Assert.NotNull(context.Response);
        Assert.NotNull(context.Config);
    }

    [Fact]
    public async Task ZeroMaxRetries_NeverConsultsThePolicy()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var policy = new RecordingPolicy { MaxRetries = 0 };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // MaxRetries = 0 时重试中间件直接透传：连首次失败都不咨询策略。
        Assert.Single(server.Received);
        Assert.Equal(0, policy.ShouldRetryCalls);
        Assert.Equal(0, policy.GetDelayCalls);
    }

    [Fact]
    public async Task MaxRetries_IsTheUpperBound_AndThePolicyIsNotAskedAtTheLimit()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var policy = new RecordingPolicy { MaxRetries = 2 };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 1 次首发 + 2 次重试；最后一次失败（Attempt = 3 > MaxRetries）不再问策略。
        Assert.Equal(3, server.Received.Count);
        Assert.Equal(2, policy.ShouldRetryCalls);
        Assert.Equal(2, policy.GetDelayCalls);
        Assert.Equal(new[] { 1, 2 }, policy.Contexts.Select(context => context.Attempt));
    }

    [Fact]
    public async Task Delay_IsHonoredPerAttempt_AndVisibleOnOnRetry()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var observed = new List<AxiosRetryContext>();
        var policy = new RecordingPolicy
        {
            MaxRetries = 2,
            Delay = TimeSpan.FromMilliseconds(120)
        };

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = policy, OnRetry = observed.Add }
        });

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        stopwatch.Stop();

        // 2 次重试 × 120ms；留足调度余量，只断言「确实等过」。
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(180),
            $"总耗时 {stopwatch.ElapsedMilliseconds}ms，未体现 2 × 120ms 的重试间隔");

        Assert.Equal(3, server.Received.Count);
        Assert.Equal(2, observed.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(120), observed[0].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(120), observed[1].Delay);
        Assert.Equal(new[] { 1, 2 }, observed.Select(context => context.Attempt));

        // 策略决策阶段看到的 Delay 是 default；填入后的值只随 OnRetry 下发。
        Assert.All(policy.Contexts, context => Assert.Equal(TimeSpan.Zero, context.Delay));
    }

    [Fact]
    public async Task NegativeDelay_IsTreatedAsImmediate()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var observed = new List<AxiosRetryContext>();
        var policy = new RecordingPolicy
        {
            MaxRetries = 1,
            Delay = TimeSpan.FromSeconds(-1)
        };

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = policy, OnRetry = observed.Add }
        });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(2, server.Received.Count);

        // 负值不该变成配置错误，也不该真的等待：退避按零处理。
        Assert.Equal(TimeSpan.Zero, Assert.Single(observed).Delay);
    }

    [Fact]
    public async Task Refusal_LeavesTheOriginalFailureUntouched()
    {
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var policy = new RecordingPolicy { MaxRetries = 2, Decision = _ => false };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Single(server.Received);
        Assert.Equal(1, policy.ShouldRetryCalls);

        // 拒绝重试时不再询问延迟，异常原样抛出（不改错误码、不改状态码）。
        Assert.Equal(0, policy.GetDelayCalls);
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task Cancellation_NeverReachesThePolicy()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return TestJson.Response(new Person("ada", 36));
        });

        var policy = new RecordingPolicy { MaxRetries = 3 };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "x", cancellationToken: cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, error.Code);

        // 调用方取消属于库级终止原因：不应把取消交给策略决定「要不要重试」。
        Assert.Equal(0, policy.ShouldRetryCalls);
    }

    [Fact]
    public async Task NegativeMaxRetries_IsRejectedBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));

        var policy = new RecordingPolicy { MaxRetries = -1 };
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = policy } });

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
        Assert.Equal(0, policy.ShouldRetryCalls);
    }

    [Fact]
    public async Task WithFailover_ThePolicyIsNotConsultedWhenTheMirrorSucceeds()
    {
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));

        var policy = new RecordingPolicy { MaxRetries = 2 };
        using var client = new AxiosClient();

        // 主端点 127.0.0.1:1 建连即被拒；换到镜像成功就不存在「一次失败尝试」，策略不该被调用。
        var response = await client.GetAsync(
            "http://127.0.0.1:1/x",
            config =>
            {
                config.Failover.Endpoints = new[] { new Uri(mirror.Url) };
                config.Retry.Policy = policy;
            });

        Assert.Equal(200, response.Status);
        Assert.Single(mirror.Received);
        Assert.Equal(0, policy.ShouldRetryCalls);
        Assert.Equal(0, policy.GetDelayCalls);
    }

    [Fact]
    public async Task WithHedging_TheGroupFailureIsReportedOncePerAttempt()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            500,
            Body: Array.Empty<byte>(),
            BodyDelay: TimeSpan.FromMilliseconds(30)));

        var policy = new RecordingPolicy { MaxRetries = 1 };
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = policy },
            Hedging = { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 每次尝试 = 首发 + 1 条对冲分支，共 2 次尝试 → 4 条请求；整组失败只交给策略一次。
        Assert.Equal(4, server.Received.Count);
        Assert.Equal(1, policy.ShouldRetryCalls);
        Assert.Equal(1, policy.GetDelayCalls);

        var context = Assert.Single(policy.Contexts);
        Assert.Equal(1, context.Attempt);
    }

    /// <summary>记录决策调用次数与上下文的策略；<see cref="Delay"/> 可返回负值用于验证收敛口径。</summary>
    private sealed class RecordingPolicy : IAxiosRetryPolicy
    {
        private readonly object _gate = new();
        private readonly List<AxiosRetryContext> _contexts = new();
        private int _shouldRetryCalls;
        private int _getDelayCalls;

        public int MaxRetries { get; init; }

        public TimeSpan Delay { get; init; }

        public Func<AxiosRetryContext, bool>? Decision { get; init; }

        public int ShouldRetryCalls => Volatile.Read(ref _shouldRetryCalls);

        public int GetDelayCalls => Volatile.Read(ref _getDelayCalls);

        public AxiosRetryContext[] Contexts
        {
            get
            {
                lock (_gate)
                {
                    return _contexts.ToArray();
                }
            }
        }

        public bool ShouldRetry(AxiosRetryContext context)
        {
            Interlocked.Increment(ref _shouldRetryCalls);

            lock (_gate)
            {
                _contexts.Add(context);
            }

            return Decision?.Invoke(context) ?? true;
        }

        public TimeSpan GetDelay(AxiosRetryContext context)
        {
            Interlocked.Increment(ref _getDelayCalls);
            return Delay;
        }
    }
}
