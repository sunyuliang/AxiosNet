using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 重试策略本体（不发请求）：判定口径、退避算术、参数校验与工厂方法的默认值。
/// 端到端行为见 <see cref="RetryTests"/>。
/// </summary>
public sealed class RetryPolicyUnitTests
{
    private static AxiosRetryContext Failed(int status, int attempt = 1, int maxRetries = 3, string method = "GET")
        => new(attempt, maxRetries,
            new HttpRequestException("boom"),
            new AxiosConfig { Method = new HttpMethod(method) },
            new AxiosResponse { Status = status });

    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var policy = new RetryPolicy();

        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.Delay);
        Assert.Equal(2d, policy.BackoffFactor);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxDelay);
        Assert.True(policy.Jitter);
        Assert.True(policy.HonorRetryAfter);
        Assert.False(policy.RetryNonIdempotentMethods);
    }

    [Fact]
    public void Constructor_SetsRetriesAndDelay()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(10));

        Assert.Equal(5, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.Delay);
    }

    [Fact]
    public void Constant_HasNoBackoff()
    {
        var policy = RetryPolicy.Constant(4, TimeSpan.FromMilliseconds(50));

        Assert.Equal(4, policy.MaxRetries);
        Assert.Equal(1d, policy.BackoffFactor);
        Assert.False(policy.Jitter);
    }

    [Fact]
    public void Exponential_ClampsMaxDelay()
    {
        var policy = RetryPolicy.Exponential(10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));

        Assert.Equal(10, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.MaxDelay);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    [InlineData(200, false)]
    public void ShouldRetry_FollowsTransientStatusSet(int status, bool expected)
    {
        var policy = new RetryPolicy();

        Assert.Equal(expected, policy.ShouldRetry(Failed(status)));
    }

    [Fact]
    public void ShouldRetry_RespectsIdempotencyGate()
    {
        var policy = new RetryPolicy();

        Assert.False(policy.ShouldRetry(Failed(503, method: "POST")));
        Assert.False(policy.ShouldRetry(Failed(503, method: "PATCH")));

        policy.RetryNonIdempotentMethods = true;
        Assert.True(policy.ShouldRetry(Failed(503, method: "POST")));

        // PUT / DELETE / HEAD / OPTIONS 本来就是幂等的
        Assert.True(policy.ShouldRetry(Failed(503, method: "PUT")));
        Assert.True(policy.ShouldRetry(Failed(503, method: "DELETE")));
    }

    [Fact]
    public void ShouldRetry_StopsWhenBudgetIsExhausted()
    {
        var policy = new RetryPolicy(2);

        Assert.True(policy.ShouldRetry(Failed(503, attempt: 2, maxRetries: 2)));
        Assert.False(policy.ShouldRetry(Failed(503, attempt: 3, maxRetries: 2)));
    }

    [Fact]
    public void ShouldRetry_DoesNotRetryCancellation()
    {
        var policy = new RetryPolicy();
        var canceled = new AxiosRetryContext(1, 3, new OperationCanceledException());

        Assert.False(policy.ShouldRetry(canceled));
    }

    [Fact]
    public void ShouldRetry_HonoursCustomGate()
    {
        var policy = new RetryPolicy { RetryWhen = _ => false };

        Assert.False(policy.ShouldRetry(Failed(503)));

        policy.RetryWhen = context => context.StatusCode == 503;
        Assert.True(policy.ShouldRetry(Failed(503)));
        Assert.False(policy.ShouldRetry(Failed(500)));
    }

    [Fact]
    public void ShouldRetry_RetriesTransportFailuresWhenGateAllows()
    {
        var policy = new RetryPolicy();

        // 没有响应 = 网络层失败：HttpRequestException / IOException / 错误码为网络层时都值得重发
        Assert.True(policy.ShouldRetry(new AxiosRetryContext(1, 3, new HttpRequestException("down"))));
        Assert.True(policy.ShouldRetry(new AxiosRetryContext(1, 3, new IOException("reset"))));
        Assert.False(policy.ShouldRetry(new AxiosRetryContext(1, 3, new InvalidOperationException("nope"))));
    }
    [Fact]
    public void GetDelay_IgnoresRetryAfterWhenDisabled()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(10)) { Jitter = false, HonorRetryAfter = false };
        var response = new AxiosResponse
        {
            Status = 503,
            Headers = new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = new[] { "2" } }
        };
        var context = new AxiosRetryContext(1, 5, new HttpRequestException("boom"),
            new AxiosConfig { Method = HttpMethod.Get }, response);

        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.GetDelay(context));
    }

    [Fact]
    public void RetryStatusCodes_NullRejectedAndCustomSetReplacesDefaults()
    {
        var policy = new RetryPolicy();

        Assert.Throws<ArgumentNullException>(() => policy.RetryStatusCodes = null!);

        policy.RetryStatusCodes = status => status == 418;
        Assert.False(policy.ShouldRetry(Failed(503)));
        Assert.True(policy.ShouldRetry(Failed(418)));
    }

    [Fact]
    public void RetryContext_RejectsNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AxiosRetryContext(1, 1, null!));
    }

    [Fact]
    public void ExponentialBackoff_DoublesAndClampsToMaxDelay()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(100))
        {
            BackoffFactor = 2d,
            Jitter = false,
            MaxDelay = TimeSpan.FromMilliseconds(300),
            HonorRetryAfter = false
        };

        var failure = new InvalidOperationException("boom");
        var delays = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            delays.Add(policy.GetDelay(new AxiosRetryContext(attempt, 5, failure)));
        }

        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(300), delays[2]);
        Assert.Equal(TimeSpan.FromMilliseconds(300), delays[3]);
    }

    [Fact]
    public void Jitter_StaysWithinHalfToFullDelay()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(1000)) { Jitter = true, HonorRetryAfter = false };
        var context = new AxiosRetryContext(1, 3, new InvalidOperationException("boom"));

        for (var i = 0; i < 50; i++)
        {
            var delay = policy.GetDelay(context);
            Assert.InRange(delay, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000));
        }
    }

    [Fact]
    public void RetryAfterHeader_OverridesLocalBackoff()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10)) { Jitter = false };

        var response = new AxiosResponse
        {
            Status = 429,
            Headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = new[] { "2" }
            }
        };

        var delay = policy.GetDelay(new AxiosRetryContext(
            1, 3, new InvalidOperationException("boom"), response: response));

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void RetryAfterHeader_IsCappedByMaxDelay()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10)) { MaxDelay = TimeSpan.FromSeconds(1) };

        var response = new AxiosResponse
        {
            Status = 503,
            Headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = new[] { "120" }
            }
        };

        var delay = policy.GetDelay(new AxiosRetryContext(
            1, 3, new InvalidOperationException("boom"), response: response));

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void RetryAfterHttpDate_IsHonored()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10))
        {
            Jitter = false,
            MaxDelay = TimeSpan.FromMinutes(5)
        };

        var response = new AxiosResponse
        {
            Status = 503,
            Headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                // 服务端也可以给 HTTP 日期（RFC 1123）：解析后换算成「距现在多久」
                ["Retry-After"] = new[] { DateTimeOffset.UtcNow.AddSeconds(30).ToString("R") }
            }
        };

        var delay = policy.GetDelay(new AxiosRetryContext(
            1, 3, new InvalidOperationException("boom"), response: response));

        // "R" 只精确到秒，留 1 秒容差；关键是「按服务端日期算」而不是本地退避的 10ms
        Assert.InRange(delay, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(30));
    }
}
