using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 第三方缓存存储（<see cref="IAxiosCacheStore"/> / <see cref="IAxiosCacheRevalidationStore"/>）的契约：
/// 库用什么键、什么时机调用；TTL、过期、容量与驱逐由存储自己负责，缓存的编排（键、准入、单飞、304）由库负责。
/// </summary>
public sealed class CacheStoreContractTests
{
    private static AxiosCache NewCache(IAxiosCacheStore store, TimeSpan? timeToLive = null)
        => new(new AxiosCacheOptions
        {
            Store = store,
            TimeToLive = timeToLive ?? TimeSpan.FromMinutes(5)
        });

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs)
        {
            headers[name] = value;
        }

        return headers;
    }

    [Fact]
    public async Task MissThenHit_GoesThroughTheStore_AndUsesThePolicyOverload()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new ContractStore();
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(store) });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal("ada", second.Data!.Name);

        // 库总是调用带 policy 的 5 参重载；4 参重载只是老存储的默认转发入口，不该被直接调用。
        Assert.Equal(0, store.LegacySetCalls);
        Assert.Equal(1, store.SetCalls);
        var policy = store.LastPolicy!;
        Assert.Equal(TimeSpan.FromMinutes(5), policy.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.ConfiguredTimeToLive);
        Assert.Equal(CacheFreshnessMode.Configured, policy.Mode);

        // 两条请求各查一次新鲜条目；条件请求只在这条键还没有条目时试过一次。
        Assert.Equal(2, store.GetCalls);
        Assert.Equal(1, store.RevalidationCalls);
    }

    [Fact]
    public async Task TtlExpiry_IsOwnedByTheStore_AndAWriteRefreshesIt()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new ContractStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(store, TimeSpan.FromMinutes(1))
        });

        await client.GetAsync<Person>(server.Url + "x");

        // 只推进存储的虚拟时钟：库不维护自己的过期时间，过期判定完全由存储实现。
        store.Advance(TimeSpan.FromMinutes(2));

        var afterExpiry = await client.GetAsync<Person>(server.Url + "x");
        Assert.NotNull(afterExpiry.Request);

        var immediate = await client.GetAsync<Person>(server.Url + "x");
        Assert.Null(immediate.Request);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(2, store.SetCalls);
    }

    [Fact]
    public async Task Keys_IsolateByUrl_AndByAuthorization()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new Person(request.Path, 1)));
        var store = new ContractStore();
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(store) });

        var a = await client.GetAsync<Person>(server.Url + "a");
        var b = await client.GetAsync<Person>(server.Url + "b");
        var aAgain = await client.GetAsync<Person>(server.Url + "a");

        // 同 URL 命中；不同 URL 各自成键。
        Assert.Null(aAgain.Request);
        Assert.Equal("/a", a.Data!.Name);
        Assert.Equal("/b", b.Data!.Name);

        // 同一 URL、不同 Authorization：库的默认键带凭据摘要，两者不能串用。
        var withIdentity = await client.GetAsync<Person>(
            server.Url + "a",
            config => config.Headers.Set("Authorization", "Bearer alpha"));

        Assert.NotNull(withIdentity.Request);
        Assert.Equal(3, server.Received.Count);
        Assert.Equal(3, store.SetCalls);
        Assert.Equal(3, store.Keys.Count);
        Assert.All(store.Keys, key => Assert.StartsWith("GET ", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleFlight_WithACustomStore_SendsAndStoresOnce()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        var store = new ContractStore();
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(store) });
        var url = server.Url + "flight";

        var requests = Enumerable.Range(0, 4).Select(_ => client.GetAsync<Person>(url)).ToArray();

        // 4 个请求各查一次存储（都未命中），同键只让 1 个真正发出去。
        await WaitUntilAsync(() => store.GetCalls == 4 && server.Received.Count == 1);
        release.SetResult(true);

        var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(responses, response => Assert.Equal("ada", response.Data!.Name));
        Assert.Single(server.Received);
        Assert.Equal(1, store.SetCalls);
    }

    [Fact]
    public async Task NotModified_RevalidatesThroughTheStore_AndWritesTheEntryBack()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("If-None-Match", out var etag) && etag == "\"v1\""
                ? new TestResponse(304, Headers: Headers(("ETag", "\"v1\""), ("X-Revalidated", "yes")))
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        var store = new ContractStore();
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(store) });
        var url = server.Url + "x";

        await client.GetAsync<Person>(url);
        store.Advance(TimeSpan.FromMinutes(6));

        var revalidated = await client.GetAsync<Person>(url);
        var hit = await client.GetAsync<Person>(url);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("\"v1\"", server.Received[1].Headers["If-None-Match"]);

        // 304 确实发了一次请求：状态与正文来自存储里的旧条目，头按 304 更新。
        Assert.NotNull(revalidated.Request);
        Assert.Equal(200, revalidated.Status);
        Assert.Equal("ada", revalidated.Data!.Name);
        Assert.Equal("yes", revalidated.GetHeader("X-Revalidated"));

        // 首次（无条目）与过期后各取一次重验条目；304 又把条目重新写回存储。
        Assert.Equal(2, store.RevalidationCalls);
        Assert.Equal(2, store.SetCalls);

        // 重新落库后立刻新鲜：第三次直接命中，不再发请求。
        Assert.Null(hit.Request);
        Assert.Equal("ada", hit.Data!.Name);
    }

    [Fact]
    public async Task RemoveAndClear_AreForwardedToTheStore()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new ContractStore();
        var cache = NewCache(store);
        using var client = new AxiosClient(new AxiosConfig { Cache = cache });
        var url = server.Url + "x";

        await client.GetAsync<Person>(url);

        var key = store.Keys.Single();
        Assert.True(await cache.RemoveAsync(key));
        Assert.Equal(1, store.RemoveCalls);

        await client.GetAsync<Person>(url);
        Assert.Equal(2, server.Received.Count);

        await cache.ClearAsync();
        Assert.Equal(1, store.ClearCalls);

        await client.GetAsync<Person>(url);
        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task CustomStore_OwnsCapacity_AndEviction_IsJustAMiss()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new Person(request.Path, 1)));
        var store = new ContractStore { Capacity = 1 };
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(store) });

        await client.GetAsync<Person>(server.Url + "a");

        // 写第二条时容量已满：由存储自己决定驱逐哪一条（库的 MaxEntries 只作用于内置存储）。
        await client.GetAsync<Person>(server.Url + "b");
        Assert.Equal(1, store.Evictions);

        // 被驱逐的键回到「未命中」，正常回源并重新落库（再驱逐当前那一条）。
        await client.GetAsync<Person>(server.Url + "a");

        Assert.Equal(3, server.Received.Count);
        Assert.Equal(3, store.SetCalls);
        Assert.Equal(2, store.Evictions);
        Assert.Single(store.Keys);
        Assert.Contains("/a", store.Keys.Single(), StringComparison.Ordinal);
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

    /// <summary>
    /// 虚拟时钟 + 容量 / 驱逐的自定义存储：过期与容量都由它自己实现，库只按收到的键读写。
    /// 同时实现 <see cref="IAxiosCacheRevalidationStore"/>，过期但带校验器的条目仍会交给条件请求。
    /// </summary>
    private sealed class ContractStore : IAxiosCacheStore, IAxiosCacheRevalidationStore
    {
        private sealed class StoredEntry(AxiosShortCircuit value, TimeSpan expiresAt)
        {
            public AxiosShortCircuit Value { get; } = value;

            public TimeSpan ExpiresAt { get; } = expiresAt;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
        private readonly List<string> _insertionOrder = new();

        private TimeSpan _now = TimeSpan.Zero;
        private int _getCalls;
        private int _revalidationCalls;
        private int _setCalls;
        private int _legacySetCalls;
        private int _removeCalls;
        private int _clearCalls;
        private int _evictions;
        private AxiosCacheEntryPolicy? _lastPolicy;

        /// <summary>条目上限，<c>1</c> 表示每写一条就驱逐最旧的一条。</summary>
        public int Capacity { get; init; } = int.MaxValue;

        public int GetCalls
        {
            get { lock (_gate) { return _getCalls; } }
        }

        public int RevalidationCalls
        {
            get { lock (_gate) { return _revalidationCalls; } }
        }

        public int SetCalls
        {
            get { lock (_gate) { return _setCalls; } }
        }

        public int LegacySetCalls
        {
            get { lock (_gate) { return _legacySetCalls; } }
        }

        public int RemoveCalls
        {
            get { lock (_gate) { return _removeCalls; } }
        }

        public int ClearCalls
        {
            get { lock (_gate) { return _clearCalls; } }
        }

        public int Evictions
        {
            get { lock (_gate) { return _evictions; } }
        }

        public AxiosCacheEntryPolicy? LastPolicy
        {
            get { lock (_gate) { return _lastPolicy; } }
        }

        public IReadOnlyList<string> Keys
        {
            get { lock (_gate) { return _entries.Keys.ToList(); } }
        }

        /// <summary>只推进这个存储的时钟（库没有自己的过期计时器）。</summary>
        public void Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _now += elapsed;
            }
        }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _getCalls++;
                return new ValueTask<AxiosShortCircuit?>(Fresh(key));
            }
        }

        public ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _revalidationCalls++;

                // 只应把带校验器的旧条目交给条件请求。
                var entry = _entries.TryGetValue(key, out var stored) ? stored.Value : null;
                if (entry is null)
                {
                    return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
                }

                var hasValidator = entry.Headers is { } headers
                    && (headers.ContainsKey("ETag") || headers.ContainsKey("Last-Modified"));

                return new ValueTask<AxiosShortCircuit?>(hasValidator ? entry : null);
            }
        }

        public ValueTask SetAsync(
            string key,
            AxiosShortCircuit entry,
            TimeSpan timeToLive,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                // 库不该直接走这一条：它总是调用带 policy 的重载。
                _legacySetCalls++;
                Store(key, entry, timeToLive);
            }

            return default;
        }

        public ValueTask SetAsync(
            string key,
            AxiosShortCircuit entry,
            AxiosCacheEntryPolicy policy,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _setCalls++;
                _lastPolicy = policy;
                Store(key, entry, policy.TimeToLive);
            }

            return default;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _removeCalls++;
                var removed = _entries.Remove(key);
                if (removed)
                {
                    _insertionOrder.Remove(key);
                }

                return new ValueTask<bool>(removed);
            }
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _clearCalls++;
                _entries.Clear();
                _insertionOrder.Clear();
            }

            return default;
        }

        private AxiosShortCircuit? Fresh(string key)
            => _entries.TryGetValue(key, out var stored) && stored.ExpiresAt > _now
                ? stored.Value
                : null;

        private void Store(string key, AxiosShortCircuit entry, TimeSpan timeToLive)
        {
            if (!_entries.ContainsKey(key))
            {
                while (_entries.Count >= Capacity && _insertionOrder.Count > 0)
                {
                    var oldest = _insertionOrder[0];
                    _insertionOrder.RemoveAt(0);
                    if (_entries.Remove(oldest))
                    {
                        _evictions++;
                    }
                }

                _insertionOrder.Add(key);
            }

            _entries[key] = new StoredEntry(entry, _now + timeToLive);
        }
    }
}
