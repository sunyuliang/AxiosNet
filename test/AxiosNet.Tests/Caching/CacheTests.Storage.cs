
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>缓存存储：容量与字节记账、清理、键选择，以及自定义存储契约。</summary>
public sealed partial class CacheTests
{
    [Fact]
    public void ConfiguredMode_DoesNotTouchHeadersBeforeStoring()
    {
        // 「落库前不解析」：存的时候一个头都不读，读到 HTTP 字段才碰响应头（用会抛异常的字典证明）。
        var policy = NewCache().CreateEntryPolicy(new ThrowingHeaders(), DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeToLive);
        Assert.Equal(CacheFreshnessMode.Configured, policy.Mode);
        Assert.Throws<InvalidOperationException>(() => policy.Vary);
    }

    [Fact]
    public void ConfiguredMode_IgnoresHeaders_ButExposesThemOnDemand()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = NewCache(o => o.TimeToLive = TimeSpan.FromSeconds(30))
            .CreateEntryPolicy(Headers(("Cache-Control", "no-store, max-age=5")), now);

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromSeconds(30), policy!.TimeToLive);
        Assert.Equal(CacheFreshnessMode.Configured, policy.Mode);
        Assert.True(policy.CacheControl.NoStore);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.CacheControl.MaxAge);
    }

    [Fact]
    public async Task CustomStore_ReceivesTheEntryPolicy()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string>
            {
                ["Cache-Control"] = "max-age=600",
                ["Vary"] = "Accept-Language"
            }));

        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                Store = store,
                FreshnessMode = CacheFreshnessMode.Intersection,
                TimeToLive = TimeSpan.FromSeconds(30)
            })
        });

        await client.GetAsync(server.Url + "x");

        var policy = Assert.Single(store.Policies);
        Assert.Equal(CacheFreshnessMode.Intersection, policy.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.HttpFreshnessLifetime);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.CacheControl.MaxAge);
        Assert.Equal(new[] { "Accept-Language" }, policy.Vary);
    }

    [Fact]
    public async Task ConfiguredMode_StoreStillGetsHttpDetails_OnDemand()
    {
        // Configured 落库前不解析响应头；存储读到策略里的 HTTP 字段时才解析，内容与解析模式一致。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string>
            {
                ["Cache-Control"] = "max-age=600",
                ["Vary"] = "Accept-Language"
            }));

        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                Store = store,
                TimeToLive = TimeSpan.FromSeconds(30)
            })
        });

        await client.GetAsync(server.Url + "x");

        var policy = Assert.Single(store.Policies);
        Assert.Equal(CacheFreshnessMode.Configured, policy.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.HttpFreshnessLifetime);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.CacheControl.MaxAge);
        Assert.Equal(new[] { "Accept-Language" }, policy.Vary);
    }

    [Fact]
    public async Task LegacyStore_WithoutPolicyOverload_StillWorks()
    {
        // 新重载带默认实现：只实现四个方法的老存储不改一行也能跑（TTL 走旧签名）。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new LegacyStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(TimeSpan.FromSeconds(30), store.LastTimeToLive);
    }

    [Fact]
    public async Task DifferentUrls_DoNotShareEntries()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var a = await client.GetAsync<Dictionary<string, string>>(server.Url + "a");
        var b = await client.GetAsync<Dictionary<string, string>>(server.Url + "b");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("/a", a.Data?["path"]);
        Assert.Equal("/b", b.Data?["path"]);
    }

    [Fact]
    public async Task DifferentAuthorization_DoesNotShareEntries()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new
        {
            token = request.Headers.TryGetValue("Authorization", out var value) ? value : null
        }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("Authorization", "Bearer one"));
        var second = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("Authorization", "Bearer two"));
        var third = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("Authorization", "Bearer one"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer one", first.Data?["token"]);
        Assert.Equal("Bearer two", second.Data?["token"]);
        Assert.Equal("Bearer one", third.Data?["token"]);
    }

    [Fact]
    public async Task DifferentConnectionAuth_DoesNotShareEntries()
    {
        // 凭据也可以来自 Connection.Auth（不是 Authorization 头），摘要同样要进键。
        await using var server = TestHttpServer.Start(request => TestJson.Response(new
        {
            auth = request.Headers.TryGetValue("Authorization", out var value) ? value : null
        }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Transport.Auth = AxiosCredential.Basic("alice", "pw"));
        var second = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Transport.Auth = AxiosCredential.Basic("bob", "pw"));
        var third = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Transport.Auth = AxiosCredential.Basic("alice", "pw"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Basic YWxpY2U6cHc=", first.Data?["auth"]);
        Assert.Equal("Basic Ym9iOnB3", second.Data?["auth"]);
        Assert.Equal("Basic YWxpY2U6cHc=", third.Data?["auth"]);
    }

    [Fact]
    public async Task CustomHeader_IsNotPartOfTheDefaultKey_Characterization()
    {
        // 契约（固定现状，不是推荐用法）：默认键只认 Authorization / Connection.Auth。
        // X-Api-Key 这类自定义身份头不进键，同 URL 的不同身份会共用同一条缓存；
        // 要隔离必须像下一条用例那样配 KeySelector。
        await using var server = TestHttpServer.Start(request => TestJson.Response(new
        {
            apiKey = request.Headers.TryGetValue("X-Api-Key", out var value) ? value : null
        }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "alice"));
        var second = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "bob"));
        var third = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "alice"));

        Assert.Single(server.Received);
        Assert.Equal("alice", first.Data?["apiKey"]);
        // bob 命中了 alice 的条目（串用），第三次则正常命中。
        Assert.Equal("alice", second.Data?["apiKey"]);
        Assert.Equal("alice", third.Data?["apiKey"]);
    }

    [Fact]
    public async Task ManagedKeyCredential_IsPartOfTheDefaultKey()
    {
        // 与上一条对照：同样的 X-Api-Key 走托管凭据时库知道它是身份，自动进默认键，不需要 KeySelector。
        await using var server = TestHttpServer.Start(request => TestJson.Response(
            new Person(request.Headers.TryGetValue("X-Api-Key", out var value) ? value : "none", 1)));

        var current = "alice";
        using var client = new ApiClient(server.Url, config => config.Caching = NewCache())
        {
            CredentialProvider = CredentialProviders.Create((_, _) =>
                ValueTask.FromResult(ApiCredential.Header("X-Api-Key", current)))
        };

        var first = await client.RequestFullAsync(new AxiosNet.Tests.WebAPI.SecureRequest());
        current = "bob";
        var second = await client.RequestFullAsync(new AxiosNet.Tests.WebAPI.SecureRequest());
        current = "alice";
        var third = await client.RequestFullAsync(new AxiosNet.Tests.WebAPI.SecureRequest());

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("alice", first.Data!.Name);
        Assert.Equal("bob", second.Data!.Name);
        Assert.Equal("alice", third.Data!.Name);
    }

    [Fact]
    public async Task CustomHeader_WithKeySelector_IsolatesEntries()
    {
        // 契约：把自定义身份头摘要进 KeySelector 后，同 URL 的不同身份不再共用条目。
        await using var server = TestHttpServer.Start(request => TestJson.Response(new
        {
            apiKey = request.Headers.TryGetValue("X-Api-Key", out var value) ? value : null
        }));

        var cache = NewCache(options => options.KeySelector = (config, uri) =>
        {
            var apiKey = config.HasHeaders ? config.Headers.Get("X-Api-Key") : null;
            return string.IsNullOrWhiteSpace(apiKey)
                ? AxiosCache.CreateKey(config, uri)
                : AxiosCache.CreateKey(config, uri) + " ak:" + Digest(apiKey);
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = cache });

        var first = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "alice"));
        var second = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "bob"));
        var third = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config => config.Headers.Set("X-Api-Key", "alice"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("alice", first.Data?["apiKey"]);
        Assert.Equal("bob", second.Data?["apiKey"]);
        Assert.Equal("alice", third.Data?["apiKey"]);
    }

    [Fact]
    public async Task IdentityHeader_WrittenInOnSendingRequest_DoesNotReachTheKey()
    {
        // 契约（钉死时序）：键在管线阶段 5.5 计算，OnSendingRequest 在每次尝试发送前才执行。
        // 因此只在钩子里写的身份头对默认键和 KeySelector 都不可见，两个身份仍会共用一条缓存。
        await using var server = TestHttpServer.Start(request => TestJson.Response(new
        {
            apiKey = request.Headers.TryGetValue("X-Api-Key", out var value) ? value : null
        }));

        var cache = NewCache(options => options.KeySelector = (config, uri) =>
        {
            var apiKey = config.HasHeaders ? config.Headers.Get("X-Api-Key") : null;
            return string.IsNullOrWhiteSpace(apiKey)
                ? AxiosCache.CreateKey(config, uri)
                : AxiosCache.CreateKey(config, uri) + " ak:" + Digest(apiKey);
        });

        AxiosConfig Hooked(string apiKey)
        {
            var config = new AxiosConfig();
            config.Observability.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.Remove("X-Api-Key");
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                return ValueTask.CompletedTask;
            };
            return config;
        }

        var uri = new Uri(server.Url + "x");
        Assert.Equal(cache.KeyFor(Hooked("alice"), uri), cache.KeyFor(Hooked("bob"), uri));

        using var client = new AxiosClient(new AxiosConfig { Caching = cache });

        var first = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config =>
            {
                config.Observability.OnSendingRequest = (request, _, _) =>
                {
                    request.Headers.Remove("X-Api-Key");
                    request.Headers.TryAddWithoutValidation("X-Api-Key", "alice");
                    return ValueTask.CompletedTask;
                };
            });
        var second = await client.GetAsync<Dictionary<string, string>>(
            server.Url + "x", config =>
            {
                config.Observability.OnSendingRequest = (request, _, _) =>
                {
                    request.Headers.Remove("X-Api-Key");
                    request.Headers.TryAddWithoutValidation("X-Api-Key", "bob");
                    return ValueTask.CompletedTask;
                };
            });

        Assert.Single(server.Received);
        Assert.Equal("alice", server.Received[0].Headers["X-Api-Key"]);
        Assert.Equal("alice", first.Data?["apiKey"]);
        // bob 的钩子来不及参与建键，直接命中 alice 的条目。
        Assert.Equal("alice", second.Data?["apiKey"]);
    }

    [Fact]
    public async Task CustomKeySelector_ReplacesTheDefaultKey()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o => o.KeySelector = (_, _) => "everything")
        });

        await client.GetAsync<Dictionary<string, string>>(server.Url + "a");
        var second = await client.GetAsync<Dictionary<string, string>>(server.Url + "b");

        // 同一个键：第二个 URL 拿到的是第一条的响应。
        Assert.Single(server.Received);
        Assert.Equal("/a", second.Data?["path"]);
    }

    [Fact]
    public async Task BlankKeySelectorResult_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o => o.KeySelector = (_, _) => "  ")
        });

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void ShouldCache_DefaultsToTheBuiltInRule_AndCanBeComposed()
    {
        var options = new AxiosCacheOptions();

        Assert.True(options.ShouldCache(new AxiosConfig { Method = HttpMethod.Get }));
        Assert.True(options.ShouldCache(new AxiosConfig { Method = HttpMethod.Head }));
        Assert.False(options.ShouldCache(new AxiosConfig { Method = HttpMethod.Post }));

        // 默认口径可直接复用：组合新规则，或用来恢复默认。
        options.ShouldCache = config => AxiosCache.IsCacheable(config) || config.Method == HttpMethod.Post;
        Assert.True(options.ShouldCache(new AxiosConfig { Method = HttpMethod.Post }));

        options.ShouldCache = AxiosCache.IsCacheable;
        Assert.False(options.ShouldCache(new AxiosConfig { Method = HttpMethod.Post }));
    }

    [Fact]
    public async Task ShouldCache_CanOptInNonGetMethods()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o => o.ShouldCache = _ => true)
        });

        // 键不含请求体：正文相同才命中（正文不同的场景必须自定义 KeySelector）。
        await client.PostAsync(server.Url + "x", new { id = 1 });
        await client.PostAsync(server.Url + "x", new { id = 1 });

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Clear_DropsEveryEntry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new MemoryCacheStore();
        var cache = new AxiosCache(new AxiosCacheOptions { Store = store });
        using var client = new AxiosClient(new AxiosConfig { Caching = cache });

        await client.GetAsync(server.Url + "x");
        Assert.Equal(1, store.Count);

        await cache.ClearAsync();

        await client.GetAsync(server.Url + "x");
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Remove_ByKey_ForcesRefetch()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var cache = NewCache();
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url, Caching = cache });

        await client.GetAsync("x");

        var config = new AxiosConfig { BaseAddress = server.Url, Url = "x" };
        Assert.True(await cache.RemoveAsync(cache.KeyFor(config, new Uri(server.Url + "x"))));

        await client.GetAsync("x");
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task MaxEntries_StopsAcceptingNewEntries()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        var store = new MemoryCacheStore(maxEntries: 1);
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        await client.GetAsync(server.Url + "a");
        await client.GetAsync(server.Url + "b");

        Assert.Equal(1, store.Count);

        // 满了不收新条目：b 再取还是发请求，a 仍然命中。
        await client.GetAsync(server.Url + "b");
        await client.GetAsync(server.Url + "a");

        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task FullStore_StillOverwritesExistingKey_AndRenewsItsLifetime()
    {
        // 覆盖已有键不增加条目数：满了也必须放行，否则热门键的存活时长再也续不上（值一直是旧的）。
        var store = new MemoryCacheStore(maxEntries: 1);

        await store.SetAsync("a", Entry("first"), TimeSpan.FromMilliseconds(60), CancellationToken.None);
        await store.SetAsync("a", Entry("second"), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(1, store.Count);
        Assert.Equal("second", Encoding.UTF8.GetString((await store.GetAsync("a", CancellationToken.None))!.Body!));

        // 存活时长按第二次写入续期：过了第一次的 60ms 仍然在。
        await Task.Delay(200);
        Assert.NotNull(await store.GetAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task MaxBytes_StopsAcceptingEntriesLargerThanTheBudget()
    {
        var store = new MemoryCacheStore(maxEntries: 10, maxBytes: 32);

        await store.SetAsync("big", Entry(new string('x', 100)), TimeSpan.FromMinutes(1), CancellationToken.None);

        // 和条目满同一口径：放不下就不收（让已有条目自然过期）。
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.ApproximateBytes);
    }

    [Fact]
    public async Task ApproximateBytes_TracksWritesRemovalsAndClear()
    {
        var store = new MemoryCacheStore(maxEntries: 10, maxBytes: 0);

        await store.SetAsync("a", Entry("hello"), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(5, store.ApproximateBytes);

        // 覆盖已有键：按新尺寸重新记账，不累加旧尺寸。
        await store.SetAsync("a", Entry("hi"), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(2, store.ApproximateBytes);

        Assert.True(await store.RemoveAsync("a", CancellationToken.None));
        Assert.Equal(0, store.ApproximateBytes);

        await store.SetAsync("b", Entry("x"), TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);
        Assert.Equal(0, store.ApproximateBytes);
    }

    [Fact]
    public async Task ExpiredEntry_IsRemovedOnRead_AndRefundsItsSize()
    {
        var store = new MemoryCacheStore(maxEntries: 10, maxBytes: 0);

        await store.SetAsync("a", Entry("hello"), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await Task.Delay(50);

        Assert.Null(await store.GetAsync("a", CancellationToken.None));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.ApproximateBytes);
    }

    [Fact]
    public async Task ConcurrentSetAndRemove_KeepTheByteAccountingConsistent()
    {
        // 覆盖写会按「新尺寸 - 旧尺寸」记差；摘除若不在同一把锁里，旧尺寸可能被退两次，计量会漂到负数。
        var store = new MemoryCacheStore(maxEntries: 64, maxBytes: 0);
        var entry = new AxiosShortCircuit { Status = 200, Body = new byte[256] };

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var workers = Enumerable.Range(0, 4).Select(seed => Task.Run(async () =>
        {
            var random = new Random(seed * 37 + 11);
            while (!stop.IsCancellationRequested)
            {
                var key = "k" + random.Next(8);
                await store.SetAsync(key, entry, TimeSpan.FromMinutes(1), CancellationToken.None);
                await store.RemoveAsync(key, CancellationToken.None);
            }
        }));

        await Task.WhenAll(workers);
        for (var i = 0; i < 8; i++)
        {
            await store.RemoveAsync("k" + i, CancellationToken.None);
        }

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.ApproximateBytes);
    }

    [Fact]
    public async Task CustomStore_IsUsedForReadsAndWrites()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig { Caching = new AxiosCache(new AxiosCacheOptions { Store = store }) });

        await client.GetAsync<Person>(server.Url + "x");
        await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(2, store.Gets.Count);
        Assert.Single(store.Sets);
        Assert.Equal("GET " + server.Url + "x", store.Sets[0]);
    }

    [Fact]
    public async Task CustomStore_IsSharedAcrossClients()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new MemoryCacheStore();

        using var first = new AxiosClient(new AxiosConfig { Caching = new AxiosCache(new AxiosCacheOptions { Store = store }) });
        await first.GetAsync<Person>(server.Url + "x");

        // 第二个实例挂同一个存储：跨实例命中（Redis 这类外部存储的形态）。
        using var second = new AxiosClient(new AxiosConfig { Caching = new AxiosCache(new AxiosCacheOptions { Store = store }) });
        var hit = await second.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("ada", hit.Data?.Name);
    }

    [Fact]
    public async Task CustomStore_ReadFailure_FallsBackToSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore { OnGet = () => throw new InvalidOperationException("store down") };
        using var client = new AxiosClient(new AxiosConfig { Caching = new AxiosCache(new AxiosCacheOptions { Store = store }) });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ada", response.Data?.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CustomStore_WriteFailure_DoesNotBreakTheRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore { OnSet = () => throw new InvalidOperationException("store down") };
        using var client = new AxiosClient(new AxiosConfig { Caching = new AxiosCache(new AxiosCacheOptions { Store = store }) });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ada", first.Data?.Name);
        Assert.Equal("ada", second.Data?.Name);

        // 写不进去就每次都发，但请求本身不受影响。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task StoreTimeout_DoesNotHoldTheResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                Store = new HangingStore(),
                StoreTimeout = TimeSpan.FromMilliseconds(50)
            })
        });

        var started = DateTime.UtcNow;
        var response = await client.GetAsync<Person>(server.Url + "x");
        var elapsed = DateTime.UtcNow - started;

        // 存储挂起只按「这次没缓存」处理：响应照常交付，不被落库拖住。
        Assert.Equal("ada", response.Data?.Name);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"落库超时没有生效：{elapsed}");
    }

    [Fact]
    public async Task Options_RejectNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { MaxEntries = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { MaxBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { StoreTimeout = TimeSpan.FromMilliseconds(-1) }));
    }

    [Fact]
    public void CustomStore_MakesMaxEntriesIrrelevant()
    {
        // 自建存储时条目上限归存储自己管：MaxEntries 既不生效，也不该因为「非法值」拦住构造。
        var cache = new AxiosCache(new AxiosCacheOptions { Store = new MemoryCacheStore(), MaxEntries = 0 });

        Assert.IsType<MemoryCacheStore>(cache.Store);
    }

    [Fact]
    public void DefaultKey_IncludesMethodAndUrl()
    {
        var config = new AxiosConfig { Method = HttpMethod.Get };
        var get = AxiosCache.CreateKey(config, new Uri("https://api.test/users?page=1"));
        var post = AxiosCache.CreateKey(new AxiosConfig { Method = HttpMethod.Post }, new Uri("https://api.test/users?page=1"));

        Assert.Equal("GET https://api.test/users?page=1", get);
        Assert.NotEqual(get, post);
    }

    [Fact]
    public void DefaultKey_DigestsTheAuthorizationHeader()
    {
        var config = new AxiosConfig();
        config.Headers.Set("Authorization", "Bearer secret-value");

        var key = AxiosCache.CreateKey(config, new Uri("https://api.test/users"));

        // 凭据只以摘要进键，原值不出现在键里。
        Assert.DoesNotContain("secret-value", key, StringComparison.Ordinal);
        Assert.StartsWith("GET https://api.test/users ", key, StringComparison.Ordinal);
    }

}
