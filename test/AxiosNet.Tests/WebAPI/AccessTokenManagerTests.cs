using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>令牌管理器：缓存、提前刷新、并发刷新单飞、取消隔离、失败不缓存。</summary>
public sealed class AccessTokenManagerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cached_SecondCallDoesNotAcquireAgain()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(Options(_ =>
        {
            Interlocked.Increment(ref calls);
            return new AccessToken("tok-1", clock.Now.AddHours(1));
        }), clock.Read);

        Assert.Equal("tok-1", await GetAsync(tokens));
        Assert.Equal("tok-1", await GetAsync(tokens));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoExpiry_NeverRefreshes()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(Options(_ =>
        {
            Interlocked.Increment(ref calls);
            return new AccessToken("forever");
        }), clock.Read);

        await GetAsync(tokens);
        clock.Now = T0.AddYears(1);
        await GetAsync(tokens);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Expired_RefreshesWithRefreshCallback()
    {
        var clock = new FakeClock();
        var acquired = 0;
        var refreshed = 0;

        var options = Options(
            acquire: _ =>
            {
                Interlocked.Increment(ref acquired);
                return new AccessToken("first", clock.Now.AddMinutes(10));
            },
            refresh: _ =>
            {
                Interlocked.Increment(ref refreshed);
                return new AccessToken("second", clock.Now.AddMinutes(10));
            });

        using var tokens = new AccessTokenManager(options, clock.Read);

        Assert.Equal("first", await GetAsync(tokens));

        clock.Now = T0.AddMinutes(9);
        Assert.Equal("second", await GetAsync(tokens));
        Assert.Equal(1, acquired);
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task WithinRefreshAhead_RefreshesBeforeExpiry()
    {
        var clock = new FakeClock();
        var refreshed = 0;

        var options = Options(
            acquire: _ => new AccessToken("first", clock.Now.AddMinutes(10)),
            refresh: _ =>
            {
                Interlocked.Increment(ref refreshed);
                return new AccessToken("second", clock.Now.AddMinutes(10));
            },
            refreshAhead: TimeSpan.FromMinutes(1));

        using var tokens = new AccessTokenManager(options, clock.Read);
        await GetAsync(tokens);

        clock.Now = T0.AddMinutes(8);      // 还剩 2 分钟：窗口外
        Assert.Equal("first", await GetAsync(tokens));
        Assert.Equal(0, refreshed);

        clock.Now = T0.AddMinutes(9).AddSeconds(30);   // 还剩 30 秒：窗口内
        Assert.Equal("second", await GetAsync(tokens));
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task ShortLifetime_CapsRefreshAheadAtHalfTheLifetime()
    {
        var clock = new FakeClock();
        var calls = 0;

        // 寿命 10 秒 + 提前量 1 分钟：实际提前量必须被压到 5 秒。
        var options = Options(
            acquire: _ =>
            {
                Interlocked.Increment(ref calls);
                return new AccessToken("tok-" + calls, clock.Now.AddSeconds(10));
            },
            refreshAhead: TimeSpan.FromMinutes(1));

        using var tokens = new AccessTokenManager(options, clock.Read);
        await GetAsync(tokens);

        clock.Now = T0.AddSeconds(4);      // 5 秒窗口外
        await GetAsync(tokens);
        Assert.Equal(1, calls);

        clock.Now = T0.AddSeconds(6);      // 窗口内
        await GetAsync(tokens);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCallers_RefreshOnlyOnce()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(async _ =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100);
            return new AccessToken("shared", clock.Now.AddHours(1));
        }), clock.Read);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => tokens.GetTokenAsync(Context(), default).AsTask()));

        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.Equal("shared", result));
    }

    [Fact]
    public async Task CanceledWaiter_DoesNotCancelTheSharedRefresh()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(async ct =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(300, ct);
            return new AccessToken("shared", clock.Now.AddHours(1));
        }), clock.Read);

        using var cts = new CancellationTokenSource();
        var canceled = tokens.GetTokenAsync(Context(), cts.Token).AsTask();
        var patient = tokens.GetTokenAsync(Context(), CancellationToken.None).AsTask();

        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal("shared", await patient);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RefreshFailure_Propagates_AndNextCallRetries()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(Options(_ =>
        {
            var attempt = Interlocked.Increment(ref calls);
            return attempt == 1
                ? throw new HttpRequestException("token endpoint down")
                : new AccessToken("tok-2", clock.Now.AddHours(1));
        }), clock.Read);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(tokens));
        Assert.Equal("token endpoint down", ex.Message);

        // 失败不缓存：下一次重新尝试并成功。
        Assert.Equal("tok-2", await GetAsync(tokens));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NullToken_Throws()
    {
        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult<AccessToken>(null!)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(tokens));
        Assert.Contains("空白", ex.Message);
    }

    [Fact]
    public void BlankToken_IsRejectedByTheType()
    {
        Assert.Throws<ArgumentException>(() => new AccessToken(" "));
    }

    [Fact]
    public async Task Invalidate_ForcesRefresh()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(Options(_ =>
        {
            Interlocked.Increment(ref calls);
            return new AccessToken("tok-" + calls, clock.Now.AddHours(1));
        }), clock.Read);

        Assert.Equal("tok-1", await GetAsync(tokens));
        tokens.Invalidate();
        Assert.Equal("tok-2", await GetAsync(tokens));
    }

    [Fact]
    public async Task InvalidateWithStaleToken_KeepsTheNewerOne()
    {
        var clock = new FakeClock();
        var calls = 0;

        using var tokens = new AccessTokenManager(Options(_ =>
        {
            Interlocked.Increment(ref calls);
            return new AccessToken("tok-" + calls, clock.Now.AddHours(1));
        }), clock.Read);

        await GetAsync(tokens);
        tokens.Invalidate("tok-0");        // 不是当前令牌：不动
        Assert.Equal("tok-1", await GetAsync(tokens));
        Assert.Equal(1, calls);

        tokens.Invalidate("tok-1");        // 是当前令牌：作废
        Assert.Equal("tok-2", await GetAsync(tokens));
    }

    [Fact]
    public async Task RefreshTimeout_ThrowsTimeoutException()
    {
        var options = new AccessTokenOptions(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AccessToken("never");
        })
        {
            RefreshTimeout = TimeSpan.FromMilliseconds(80)
        };

        using var tokens = new AccessTokenManager(options);

        await Assert.ThrowsAsync<TimeoutException>(() => GetAsync(tokens));
    }

    [Fact]
    public async Task Disposed_Throws()
    {
        var tokens = new AccessTokenManager(Options(_ => new AccessToken("tok")));
        tokens.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => GetAsync(tokens));
    }

    [Fact]
    public void Options_RejectNegativeRefreshAhead_AndNonPositiveTimeout()
    {
        var acquire = new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok")));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AccessTokenOptions(acquire.AcquireAsync) { RefreshAhead = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccessTokenOptions(acquire.AcquireAsync) { RefreshTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentNullException>(() => new AccessTokenOptions(null!));
    }

    [Fact]
    public async Task ApiClient_WritesTheManagedToken()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var tokens = new AccessTokenManager(Options(_ => new AccessToken("managed")));
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        Assert.Equal("Bearer managed", Assert.Single(server.Received).Headers["Authorization"]);
    }

    private static AccessTokenOptions Options(
        Func<CancellationToken, AccessToken> acquire,
        Func<CancellationToken, AccessToken>? refresh = null,
        TimeSpan? refreshAhead = null)
        => new(ct => ValueTask.FromResult(acquire(ct)))
        {
            RefreshAsync = refresh is null ? null : ct => ValueTask.FromResult(refresh(ct)),
            RefreshAhead = refreshAhead ?? TimeSpan.FromMinutes(1)
        };

    private static ApiAuthContext Context() => new(new object(), new AxiosConfig());

    private static Task<string?> GetAsync(AccessTokenManager tokens)
        => tokens.GetTokenAsync(Context(), CancellationToken.None).AsTask();

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = T0;

        public DateTimeOffset Read() => Now;
    }
}
