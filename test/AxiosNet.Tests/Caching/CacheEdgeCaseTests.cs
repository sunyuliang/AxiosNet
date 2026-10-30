using System.Diagnostics;
using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 缓存的边界用例：单飞生命周期、命中路径语义、无效新鲜期（RFC 9111 §4.2.1）、存储读取的总预算与取消。
/// </summary>
public sealed class CacheEdgeCaseTests
{
    private static AxiosCache NewCache(Action<AxiosCacheOptions>? configure = null)
    {
        var options = new AxiosCacheOptions();
        configure?.Invoke(options);
        return new AxiosCache(options);
    }

    // ---------------------------------------------------------------
    // 单飞生命周期
    // ---------------------------------------------------------------

    [Fact]
    public async Task LeaderHookFailure_ReleasesTheFollower()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var calls = 0;
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(),
            Callbacks =
            {
                OnSendingRequest = (_, _, _) =>
                {
                    // 领导者那次发送被回调拦截：跟随者不能跟着挂在原地等。
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        throw new InvalidOperationException("hook boom");
                    }

                    return ValueTask.CompletedTask;
                }
            }
        });
        var url = server.Url + "hook";

        var leader = client.GetAsync<Person>(url);
        var follower = client.GetAsync<Person>(url);

        await Assert.ThrowsAnyAsync<AxiosException>(() => leader.WaitAsync(TimeSpan.FromSeconds(5)));
        var response = await follower.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ada", response.Data!.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task LeaderNotStorable_EveryFollowerSendsItsOwn()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        // 只把领导者那颗响应换成没有 RawBody 的新对象（不可落库），其余原样通过。
        var intercepted = 0;
        client.Interceptors.Response.Use(response =>
            Interlocked.Increment(ref intercepted) == 1
                ? new AxiosResponse
                {
                    Status = response.Status,
                    StatusText = response.StatusText,
                    Data = response.Data,
                    Headers = response.Headers,
                    Config = response.Config,
                    Request = response.Request
                }
                : response);

        var url = server.Url + "swapped";
        var requests = Enumerable.Range(0, 4).Select(_ => client.GetAsync<Person>(url)).ToArray();
        await WaitUntilAsync(() => server.Received.Count == 1);
        await Task.Delay(100);
        release.SetResult(true);

        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, response => Assert.Equal("ada", response.Data!.Name));
        Assert.Equal(4, server.Received.Count);
    }

    [Fact]
    public async Task FollowerCancellation_DoesNotStopTheLeaderFromStoring()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "store-after-cancel";

        var leader = client.GetAsync<Person>(url);
        await WaitUntilAsync(() => server.Received.Count == 1);

        using var followerCts = new CancellationTokenSource();
        var follower = client.GetAsync<Person>(url, config => config.CancellationToken = followerCts.Token);
        await Task.Delay(80);
        followerCts.Cancel();

        var canceled = await Assert.ThrowsAnyAsync<AxiosException>(() => follower.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AxiosException.ErrCanceled, canceled.Code);

        release.SetResult(true);
        await leader.WaitAsync(TimeSpan.FromSeconds(5));

        // 跟随者退出不影响领导者落库：第三次请求直接命中。
        var third = await client.GetAsync<Person>(url);
        Assert.Equal("ada", third.Data!.Name);
        Assert.Null(third.Request);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task StoreWriteFailure_ReleasesEveryFollower()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions { Store = new FaultedSetStore() })
        });
        var url = server.Url + "write-failure";

        var requests = Enumerable.Range(0, 3).Select(_ => client.GetAsync<Person>(url)).ToArray();
        await Task.Delay(150);
        release.SetResult(true);

        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        // 没有可共享的条目：跟随者各自补发。
        Assert.All(results, response => Assert.Equal("ada", response.Data!.Name));
        Assert.Equal(3, server.Received.Count);
    }

    // ---------------------------------------------------------------
    // 命中路径语义
    // ---------------------------------------------------------------

    [Fact]
    public async Task Hit_AppliesValidateStatus()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "validate-on-hit";

        await client.GetAsync(url);
        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.GetAsync(url, config => config.Callbacks.ValidateStatus = status => status == 201));

        Assert.Equal(200, error.Response!.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hit_AppliesPerCallTransformResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "transform-on-hit";

        await client.GetAsync(url);
        var response = await client.GetAsync<Person>(url, config => config.TransformResponse.Add(
            (data, _) => data is Person person ? new Person(person.Name.ToUpperInvariant(), person.Age) : data));

        Assert.Equal("ADA", response.Data!.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hit_HonorsPreCancelledToken()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "cancel-on-hit";

        await client.GetAsync(url);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.GetAsync(url, config => config.CancellationToken = cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, error.Code);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Hit_ReturnsIndependentBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "bytes-on-hit";

        var first = await client.GetAsync<byte[]>(url);
        first.Data![0] = (byte)'X';

        // 调用方改写拿到的字节数组，不能污染缓存里的那一份。
        var second = await client.GetAsync<byte[]>(url);

        Assert.Equal((byte)'{', second.Data![0]);
        Assert.Single(server.Received);
    }

    // ---------------------------------------------------------------
    // 无效新鲜期（RFC 9111 §4.2.1：无效的新鲜期信息按已过期处理）
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("oops")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public async Task RequestMaxAge_InvalidValue_BypassesTheLookup(string maxAge)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(options => options.FreshnessMode = CacheFreshnessMode.Intersection)
        });
        var url = server.Url + "request-max-age";

        await client.GetAsync(url);
        await client.GetAsync(url, config => config.Headers.Set("Cache-Control", $"max-age={maxAge}"));

        Assert.Equal(2, server.Received.Count);
    }

    [Theory]
    [InlineData("oops")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public async Task InvalidResponseMaxAge_IsTreatedAsStale(string maxAge)
    {
        // Expires 在未来也救不回来：max-age 出现即覆盖 Expires，无效值按零。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string>
            {
                ["Cache-Control"] = $"max-age={maxAge}",
                ["Expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("R")
            }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(options => options.FreshnessMode = CacheFreshnessMode.Intersection)
        });
        var url = server.Url + "response-max-age";

        await client.GetAsync(url);
        await client.GetAsync(url);

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task ContentRangeOnSuccess_IsNotStored()
    {
        // 键只有方法 + URL：带 Content-Range 的完整状态码响应同样是某段区间的内容。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Content-Range"] = "bytes 0-15/999" }));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "content-range";

        await client.GetAsync(url);
        await client.GetAsync(url);

        Assert.Equal(2, server.Received.Count);
    }

    // ---------------------------------------------------------------
    // 存储读取：总预算与取消
    // ---------------------------------------------------------------

    [Fact]
    public async Task StoreRead_IsBoundedByTotalTimeout()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            TotalTimeout = TimeSpan.FromMilliseconds(200),
            Cache = new AxiosCache(new AxiosCacheOptions { Store = new HangingGetStore() })
        });

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "hanging-store").WaitAsync(TimeSpan.FromSeconds(5)));

        // 存储挂起不能把请求一起拖住：与「总预算耗尽、尝试没能发出」同一错误模型。
        Assert.True(error.IsTotalTimeoutExceeded, $"实际错误：{error.Code}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"实际耗时 {stopwatch.Elapsed}。");
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task StoreReadCallerCancellation_IsReportedAsErrCanceled()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions { Store = new CancellableGetStore() })
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "store-cancel", config => config.CancellationToken = cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(AxiosException.ErrCanceled, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task StoreReadSelfCancellation_CountsAsMiss()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions { Store = new SelfCancellingGetStore() })
        });

        // 存储自己抛的取消（调用方没取消）只表示缓存不可用：照常发请求。
        var response = await client.GetAsync<Person>(server.Url + "store-self-cancel");

        Assert.Equal("ada", response.Data!.Name);
        Assert.Single(server.Received);
    }

    // ---------------------------------------------------------------
    // 其他特性交互
    // ---------------------------------------------------------------

    [Fact]
    public async Task Redirect_EntryIsStoredUnderTheOriginalUrl()
    {
        await using var server = TestHttpServer.Start(request => request.Path.EndsWith("/redirect", StringComparison.Ordinal)
            ? new TestResponse(302, "Found", Headers: new Dictionary<string, string>
            {
                ["Location"] = request.Path.Replace("/redirect", "/final")
            })
            : TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var url = server.Url + "redirect/redirect";

        var first = await client.GetAsync<Person>(url);
        var second = await client.GetAsync<Person>(url);

        Assert.Equal("ada", first.Data!.Name);
        Assert.Equal("ada", second.Data!.Name);

        // 第一次：302 + 跟随后的 200；第二次命中原始 URL 的条目，不再经过重定向。
        Assert.Equal(2, server.Received.Count);
        Assert.Null(second.Request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时。");
    }

    private sealed class FaultedSetStore : IAxiosCacheStore
    {
        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => new(Task.FromException(new IOException("store down")));

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class HangingGetStore : IAxiosCacheStore
    {
        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new(new TaskCompletionSource<AxiosShortCircuit?>().Task);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => default;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class CancellableGetStore : IAxiosCacheStore
    {
        public async ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => default;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class SelfCancellingGetStore : IAxiosCacheStore
    {
        public async ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new OperationCanceledException();
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => default;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }
}
