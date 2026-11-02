using System.Diagnostics;
using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 缓存的边界用例：单飞生命周期、命中路径语义、无效新鲜期（RFC 9111 §4.2.1）、存储读写的总预算与取消。
/// </summary>
/// <remarks>
/// 存储读写那两条断言的是「上限耗时」，与其它集合并行跑时会被抢走 CPU 与线程池线程而超预算，
/// 故整类进 <see cref="TimingCollection"/> 独占运行。
/// </remarks>
[Collection(TimingCollection.Name)]
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
        // 判据不靠绝对秒数：存储永不完成，能返回就说明有一道时限生效；把 StoreTimeout 抬到 30s，
        // 「被剩余总预算（200ms）收住」与「被 StoreTimeout 收住」就差两个数量级，不会被 CI 抖动抹平。
        var budget = TimeSpan.FromMilliseconds(200);
        var storeTimeout = TimeSpan.FromSeconds(30);
        var liveness = TimeSpan.FromSeconds(10);

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            TotalTimeout = budget,
            Cache = new AxiosCache(new AxiosCacheOptions { Store = new HangingGetStore(), StoreTimeout = storeTimeout })
        });

        // 预热：入口到读缓存之间的合并 / 拦截 / 编码 / 键计算与冷启动的 JIT 同样花的是这份 200ms 预算，
        // 不预热时测量窗口里混进的是这些机器开销，而不是「存储读取被截断」。
        // 另起一个客户端并给它普通存储：连接池按 origin 共享，预热不会碰被测的挂起存储。
        using (var warmup = new AxiosClient(new AxiosConfig { Cache = NewCache() }))
        {
            await warmup.GetAsync(server.Url + "warmup");
        }

        var receivedBefore = server.Received.Count;

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "hanging-store").WaitAsync(liveness));
        stopwatch.Stop();

        // 存储挂起不能把请求一起拖住：与「总预算耗尽、尝试没能发出」同一错误模型。
        Assert.True(error.IsTotalTimeoutExceeded, $"实际错误：{error.Code}");

        // 上限给到 5s（正常实现约 200ms）：留出的余量只为吸收 CI 调度抖动，
        // 「谁在收住这次读取」由 StoreTimeout 30s > 活性闸 10s 这个数量级差来判，不靠这条阈值。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"实际耗时 {stopwatch.Elapsed}（总预算 {budget}，StoreTimeout {storeTimeout}）。");

        // 预算在查缓存时就耗尽了，这次尝试一个字节都没发出（预热那一条是另一个客户端发的）。
        Assert.Equal(receivedBefore, server.Received.Count);
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
    // 存储写入：总预算
    // ---------------------------------------------------------------

    [Fact]
    public async Task StoreWrite_IsBoundedByTotalTimeout()
    {
        // 预算要远大于「本机一条请求」的耗时，否则线程池被别的用例占住时，被预算收掉的是发送本身而不是落库。
        // 判据不靠绝对秒数：存储延迟比预算大一个数量级，落库没被截断时响应会被拖到分钟级。
        var budget = TimeSpan.FromSeconds(2);
        var storeDelay = TimeSpan.FromSeconds(60);
        var liveness = TimeSpan.FromSeconds(20);

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new SlowSetStore(storeDelay);
        using var client = new AxiosClient(new AxiosConfig
        {
            TotalTimeout = budget,
            Cache = new AxiosCache(new AxiosCacheOptions { Store = store, StoreTimeout = storeDelay })
        });

        // 预热建连接、跑 JIT：这些耗时与「落库受预算约束」无关，但同样花的是这份预算。
        // 另起一个客户端并给它普通存储：连接池按 origin 共享，预热不会碰被测的慢存储。
        using (var warmup = new AxiosClient(new AxiosConfig { Cache = NewCache() }))
        {
            await warmup.GetAsync(server.Url + "warmup");
        }

        var url = server.Url + "slow-store-set";
        var receivedBefore = server.Received.Count;

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync<Person>(url).WaitAsync(liveness);
        stopwatch.Stop();

        // 写入受剩余总预算约束：存储比 StoreTimeout 还慢时也不能把响应拖过预算。
        Assert.Equal("ada", response.Data!.Name);
        Assert.Equal(1, store.SetCalls);

        // 测量窗口只含第一次请求：它应当被 2s 的预算收住（约 2s）。上限 15s 只吸收 CI 抖动，
        // 与 60s 的存储延迟仍差 4 倍；「落库有没有被截断」由活性闸（20s < 60s）来判，不靠这条阈值。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"落库拖住了响应：{stopwatch.Elapsed}（预算 {budget}，存储延迟 {storeDelay}）。");

        // 这次没落库：下一次仍旧发请求。
        await client.GetAsync<Person>(url).WaitAsync(liveness);
        Assert.Equal(receivedBefore + 2, server.Received.Count);
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

    private sealed class SlowSetStore(TimeSpan delay) : IAxiosCacheStore
    {
        private int _setCalls;

        /// <summary>被调用过多少次写入：落库路径真的走到存储了才算测到「写入受预算约束」。</summary>
        public int SetCalls => Volatile.Read(ref _setCalls);

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public async ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _setCalls);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

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
