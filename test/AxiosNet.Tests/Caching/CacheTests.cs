using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>响应缓存：命中不发请求、原始字节按每次调用重新解析、同键并发只发一次。</summary>
public sealed class CacheTests
{
    private static AxiosCache NewCache(Action<AxiosCacheOptions>? configure = null)
    {
        var options = new AxiosCacheOptions();
        configure?.Invoke(options);
        return new AxiosCache(options);
    }

    [Fact]
    public async Task Disabled_ByDefault_SendsEveryTime()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task SecondRequest_IsServedFromCache_WithoutSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("ada", first.Data?.Name);
        Assert.Equal("ada", second.Data?.Name);

        // 命中不发请求，底层请求消息不存在。
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
    }

    [Fact]
    public async Task Hit_ReparsesTheStoredBytes_ForEachCallShape()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var typed = await client.GetAsync<Person>(server.Url + "x");
        var text = await client.GetAsync<string>(server.Url + "x");
        var bytes = await client.GetAsync<byte[]>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("ada", typed.Data?.Name);
        Assert.Contains("\"name\":\"ada\"", text.Data, StringComparison.Ordinal);
        Assert.Equal(TestJson.Bytes(new Person("ada", 36)), bytes.Data);
    }

    [Fact]
    public async Task Hit_KeepsStatusAndResponseHeaders()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            statusCode: 201,
            headers: new Dictionary<string, string> { ["X-Trace"] = "abc" }));

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(201, hit.Status);
        Assert.Equal("Created", hit.StatusText);
        Assert.Equal("abc", hit.GetHeader("X-Trace"));
        Assert.Equal("application/json; charset=utf-8", hit.GetHeader("Content-Type"));
    }

    [Fact]
    public async Task Entry_Expires_AfterTimeToLive()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(60)) });

        await client.GetAsync(server.Url + "x");
        await Task.Delay(200);
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

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

    [Theory]
    [InlineData("no-store")]
    [InlineData("no-cache")]
    public void ServerAndIntersection_SkipNoStoreAndNoCache(string directive)
    {
        var headers = Headers(("Cache-Control", directive));
        var now = DateTimeOffset.UtcNow;

        Assert.Null(NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server).CreateEntryPolicy(headers, now));
        Assert.Null(NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection).CreateEntryPolicy(headers, now));
    }

    [Fact]
    public void IntersectionMode_TakesTheSmallerOfConfiguredTtlAndMaxAge()
    {
        var now = DateTimeOffset.UtcNow;

        var httpShorter = NewCache(o =>
        {
            o.FreshnessMode = CacheFreshnessMode.Intersection;
            o.TimeToLive = TimeSpan.FromMinutes(5);
        }).CreateEntryPolicy(Headers(("Cache-Control", "max-age=30")), now)!;

        Assert.Equal(TimeSpan.FromSeconds(30), httpShorter.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(5), httpShorter.ConfiguredTimeToLive);
        Assert.Equal(CacheFreshnessMode.Intersection, httpShorter.Mode);

        var configuredShorter = NewCache(o =>
        {
            o.FreshnessMode = CacheFreshnessMode.Intersection;
            o.TimeToLive = TimeSpan.FromSeconds(30);
        }).CreateEntryPolicy(Headers(("Cache-Control", "max-age=600")), now)!;

        Assert.Equal(TimeSpan.FromSeconds(30), configuredShorter.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(600), configuredShorter.HttpFreshnessLifetime);
    }

    [Fact]
    public void ServerMode_UsesServerTtl_EvenWhenLongerThanConfigured()
    {
        var policy = NewCache(o =>
        {
            o.FreshnessMode = CacheFreshnessMode.Server;
            o.TimeToLive = TimeSpan.FromSeconds(30);
        }).CreateEntryPolicy(Headers(("Cache-Control", "max-age=600")), DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.FromSeconds(600), policy.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.ConfiguredTimeToLive);
        Assert.Equal(CacheFreshnessMode.Server, policy.Mode);
    }

    [Fact]
    public void ServerMode_DoesNotCacheResponsesWithoutFreshnessHeaders()
    {
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
            .CreateEntryPolicy(Headers(), DateTimeOffset.UtcNow);

        Assert.Null(policy);
    }

    [Fact]
    public void IntersectionMode_SubtractsAgeFromTheFreshnessLifetime()
    {
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
            .CreateEntryPolicy(Headers(("Cache-Control", "max-age=10"), ("Age", "4")), DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.FromSeconds(4), policy.Age);
        Assert.Equal(TimeSpan.FromSeconds(6), policy.TimeToLive);
    }

    [Fact]
    public void ServerMode_SubtractsAgeFromTheFreshnessLifetime()
    {
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
            .CreateEntryPolicy(Headers(("Cache-Control", "max-age=10"), ("Age", "4")), DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.FromSeconds(4), policy.Age);
        Assert.Equal(TimeSpan.FromSeconds(6), policy.TimeToLive);
    }

    [Theory]
    [InlineData(CacheFreshnessMode.Server)]
    [InlineData(CacheFreshnessMode.Intersection)]
    public void ServerAndIntersection_TreatFreshnessExhaustedByAgeAsStale(CacheFreshnessMode mode)
    {
        var policy = NewCache(o => o.FreshnessMode = mode).CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=5"), ("Age", "10")),
            DateTimeOffset.UtcNow);

        Assert.Null(policy);
    }

    [Fact]
    public void FutureDate_DoesNotCreateApparentAge()
    {
        // 服务器时钟快了几分钟：龄期只认 Age 头，不把负的 apparent age 算进去。
        var now = DateTimeOffset.UtcNow;
        var policy = NewCache().CreateEntryPolicy(
            Headers(("Date", now.AddMinutes(5).ToString("R")), ("Age", "7")),
            now)!;

        Assert.Equal(TimeSpan.FromSeconds(7), policy.Age);
    }

    [Fact]
    public void IntersectionMode_UsesExpiresMinusDate_WhenMaxAgeIsAbsent()
    {
        // 整秒取值：HTTP 日期头只有秒精度，带小数的「收到时刻」会被算成额外龄期。
        var date = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
            .CreateEntryPolicy(
                Headers(("Date", date.ToString("R")), ("Expires", date.AddSeconds(20).ToString("R"))),
                date)!;

        Assert.Equal(TimeSpan.FromSeconds(20), policy.HttpFreshnessLifetime);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.TimeToLive);
    }

    [Fact]
    public void ServerAndIntersection_TreatPastOrInvalidExpiresAsAlreadyExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var headers = new[]
        {
            Headers(("Date", now.ToString("R")), ("Expires", now.AddSeconds(-1).ToString("R"))),
            Headers(("Expires", "0"))
        };

        foreach (var mode in new[] { CacheFreshnessMode.Server, CacheFreshnessMode.Intersection })
        {
            var cache = NewCache(o => o.FreshnessMode = mode);
            foreach (var entry in headers)
            {
                Assert.Null(cache.CreateEntryPolicy(entry, now));
            }
        }
    }

    [Fact]
    public void IntersectionMode_WithoutFreshnessHeaders_StillUsesConfiguredTtl()
    {
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
            .CreateEntryPolicy(Headers(), DateTimeOffset.UtcNow)!;

        Assert.Null(policy.HttpFreshnessLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeToLive);
    }

    [Fact]
    public void IntersectionMode_ParsesPrivateSMaxAgeAndVary_ForTheStore()
    {
        var policy = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
            .CreateEntryPolicy(
                Headers(
                    ("Cache-Control", "private, s-maxage=60, stale-while-revalidate=10"),
                    ("Vary", "Accept-Language, Accept-Encoding")),
                DateTimeOffset.UtcNow)!;

        Assert.True(policy.CacheControl.Private);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.CacheControl.SharedMaxAge);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.CacheControl.StaleWhileRevalidate);
        Assert.Equal(new[] { "Accept-Language", "Accept-Encoding" }, policy.Vary);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeToLive);
    }

    [Fact]
    public void CacheControlParser_HandlesCaseQuotingExtensionsAndOddValues()
    {
        var policy = NewCache().CreateEntryPolicy(
            Headers(("Cache-Control", "NO-STORE, Max-Age=\"5\", community=\"UCI, Inc.\", foo, bar=baz")),
            DateTimeOffset.UtcNow)!;

        Assert.True(policy.CacheControl.NoStore);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.CacheControl.MaxAge);

        // 引号里的逗号不算分隔符；不认识的指令留下参数（无参数为 null）。
        Assert.Equal("UCI, Inc.", policy.CacheControl.Extensions["COMMUNITY"]);
        Assert.True(policy.CacheControl.Extensions.ContainsKey("foo"));
        Assert.Null(policy.CacheControl.Extensions["foo"]);
        Assert.Equal("baz", policy.CacheControl.Extensions["bar"]);

        // 非法或负数的 max-age / s-maxage 按零（RFC 9111 §4.2.1：无效的新鲜期信息按已过期处理）。
        var invalid = NewCache().CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=abc, s-maxage=-1")),
            DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.Zero, invalid.CacheControl.MaxAge);
        Assert.Equal(TimeSpan.Zero, invalid.CacheControl.SharedMaxAge);
    }

    [Fact]
    public void CacheControlParser_MergesMultipleHeaderLines()
    {
        var policy = NewCache().CreateEntryPolicy(
            HeaderLines(("Cache-Control", new[] { "max-age=5", "no-cache" })),
            DateTimeOffset.UtcNow)!;

        Assert.Equal(TimeSpan.FromSeconds(5), policy.CacheControl.MaxAge);
        Assert.True(policy.CacheControl.NoCache);
    }

    [Fact]
    public void VaryParser_SplitsEveryHeaderLine()
    {
        var policy = NewCache().CreateEntryPolicy(
            HeaderLines(("Vary", new[] { " Accept-Language , Accept-Encoding ", "*" })),
            DateTimeOffset.UtcNow)!;

        Assert.Equal(new[] { "Accept-Language", "Accept-Encoding", "*" }, policy.Vary);
    }

    [Theory]
    [InlineData(CacheFreshnessMode.Server)]
    [InlineData(CacheFreshnessMode.Intersection)]
    public void RequestNoStore_RejectsTheEntry(CacheFreshnessMode mode)
    {
        var policy = NewCache(o => o.FreshnessMode = mode).CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=60")),
            DateTimeOffset.UtcNow,
            new CacheRequestDirectives(noStore: true, noCache: false, maxAge: null));

        Assert.Null(policy);
    }

    [Theory]
    [InlineData(CacheFreshnessMode.Server)]
    [InlineData(CacheFreshnessMode.Intersection)]
    public void RequestNoCache_StillStores(CacheFreshnessMode mode)
    {
        // no-cache 只要求「复用前先验证」，库不做条件请求，所以查询被跳过，但响应仍可落库。
        var policy = NewCache(o => o.FreshnessMode = mode).CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=60")),
            DateTimeOffset.UtcNow,
            new CacheRequestDirectives(noStore: false, noCache: true, maxAge: null));

        Assert.NotNull(policy);
    }

    [Fact]
    public void RequestMaxAgeZero_RejectsTheEntry()
    {
        Assert.Null(NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection).CreateEntryPolicy(
            Headers(),
            DateTimeOffset.UtcNow,
            new CacheRequestDirectives(noStore: false, noCache: false, maxAge: TimeSpan.Zero)));
    }

    [Fact]
    public void RequestMaxAge_CapsTheEntryTtl()
    {
        var policy = NewCache(o =>
        {
            o.FreshnessMode = CacheFreshnessMode.Intersection;
            o.TimeToLive = TimeSpan.FromMinutes(5);
        }).CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=600")),
            DateTimeOffset.UtcNow,
            new CacheRequestDirectives(noStore: false, noCache: false, maxAge: TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromSeconds(10), policy!.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.ConfiguredTimeToLive);
    }

    [Fact]
    public void ConfiguredMode_IgnoresRequestDirectives()
    {
        // 强制按配置执行：请求说 no-store / max-age=0 也照常落库。
        var policy = NewCache().CreateEntryPolicy(
            Headers(),
            DateTimeOffset.UtcNow,
            new CacheRequestDirectives(noStore: true, noCache: true, maxAge: TimeSpan.Zero));

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromSeconds(30), policy!.TimeToLive);
    }

    [Theory]
    [InlineData(CacheFreshnessMode.Server)]
    [InlineData(CacheFreshnessMode.Intersection)]
    public void VaryStar_IsNotStored(CacheFreshnessMode mode)
    {
        // Vary: * 表示任何维度都可能影响内容：键只有方法 + URL，落库会串内容。
        Assert.Null(NewCache(o => o.FreshnessMode = mode).CreateEntryPolicy(
            Headers(("Cache-Control", "max-age=60"), ("Vary", "Accept-Encoding, *")),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConfiguredMode_StoresVaryStar()
    {
        // 强制按配置执行：Vary 不参与取舍，信息仍随策略交给存储。
        var policy = NewCache().CreateEntryPolicy(Headers(("Vary", "*")), DateTimeOffset.UtcNow)!;

        Assert.Equal(new[] { "*" }, policy.Vary);
    }

    [Fact]
    public void ReadRequestDirectives_IsNeutralInConfiguredMode()
    {
        var config = new AxiosConfig { Method = HttpMethod.Get };
        config.Headers.Set("Cache-Control", "no-store, no-cache, max-age=0");

        var ignored = NewCache().ReadRequestDirectives(config);
        Assert.False(ignored.SkipLookup);
        Assert.True(ignored.CanStore);

        var parsed = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection).ReadRequestDirectives(config);
        Assert.True(parsed.NoStore);
        Assert.True(parsed.NoCache);
        Assert.Equal(TimeSpan.Zero, parsed.MaxAge);
        Assert.True(parsed.SkipLookup);
        Assert.False(parsed.CanStore);
    }

    [Fact]
    public void PragmaNoCache_IsHonored_WhenCacheControlIsAbsent()
    {
        var config = new AxiosConfig { Method = HttpMethod.Get };
        config.Headers.Set("Pragma", "no-cache");

        var directives = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection).ReadRequestDirectives(config);

        Assert.True(directives.NoCache);
        Assert.True(directives.SkipLookup);
        Assert.True(directives.CanStore);
    }

    [Fact]
    public async Task ConfiguredMode_CachesNoStoreResponses()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "no-store" }));

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task IntersectionMode_DoesNotCacheNoStoreResponses()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "no-store" }));

        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions
            {
                Store = store,
                FreshnessMode = CacheFreshnessMode.Intersection
            })
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.Empty(store.Policies);
    }

    [Fact]
    public async Task IntersectionMode_NoStore_WithConcurrency_EveryRequestReachesTheServer()
    {
        // 不落库时没有可共享的字节：等待者各自补发，不能拿到空结果。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "no-store" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.GetAsync<Person>(server.Url + "x")));

        Assert.Equal(5, server.Received.Count);
        Assert.All(responses, response => Assert.Equal("ada", response.Data!.Name));
    }

    [Fact]
    public async Task IntersectionMode_TreatsMaxAgeZeroAsAlreadyStale()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=0" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task RequestNoCache_SkipsTheLookup_ButStillStores()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        // no-cache：每次都绕过查询直接发，但响应仍会落库（可被后续不带指令的请求命中）。
        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Cache-Control", "no-cache"));
        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Cache-Control", "no-cache"));
        Assert.Equal(2, server.Received.Count);

        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.Null(hit.Request);
        Assert.Equal("ada", hit.Data?.Name);
    }

    [Fact]
    public async Task RequestPragmaNoCache_SkipsTheLookup()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Pragma", "no-cache"));

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task RequestNoStore_SkipsTheLookup_AndIsNotStored()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Cache-Control", "no-store"));

        // no-store 没有落库：后面不带指令的请求必须真发；它落库后第三次才命中。
        var second = await client.GetAsync<Person>(server.Url + "x");
        var third = await client.GetAsync<Person>(server.Url + "x");

        Assert.NotNull(second.Request);
        Assert.Null(third.Request);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task ConfiguredMode_IgnoresRequestNoStore_EndToEnd()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Cache-Control", "no-store"));
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Null(hit.Request);
    }

    [Fact]
    public async Task RequestMaxAge_CapsTheStoredTtl_EndToEnd()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=600" }));

        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions
            {
                Store = store,
                FreshnessMode = CacheFreshnessMode.Intersection
            })
        });

        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Cache-Control", "max-age=15"));

        // 服务器愿意给 600 秒，客户端只接受 15 秒：落库 TTL 取客户端声明的上限。
        var policy = Assert.Single(store.Policies);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.TimeToLive);
    }

    [Fact]
    public async Task RangeRequests_AreNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Range", "bytes=0-3"));
        await client.GetAsync(server.Url + "x", config => config.Headers.Set("Range", "bytes=0-3"));

        // 键只有方法 + URL，扣不住 Range：范围请求不参与缓存，免得 206 分段正文被后续整份 GET 拿走。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public void RangeRequests_AreNotCacheable_ByTheBuiltInRule()
    {
        var config = new AxiosConfig { Method = HttpMethod.Get };
        config.Headers.Set("Range", "bytes=0-3");

        Assert.False(AxiosCache.IsCacheable(config));
    }

    [Fact]
    public async Task PartialContent_IsNotStored()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            statusCode: 206,
            headers: new Dictionary<string, string> { ["Content-Range"] = "bytes 0-3/10" }));

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        // 206 / Content-Range 是某段区间的内容：落库后普通 GET 会把它当整份正文交付。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task SetCookie_IsNotReplayedOnHit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Set-Cookie"] = "sid=secret; Path=/" }));

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("sid=secret; Path=/", first.GetHeader("Set-Cookie"));

        // 会话 cookie 属于「这一次连接」：命中回放等于把别人的会话塞进下一次请求。
        Assert.Null(hit.GetHeader("Set-Cookie"));
    }

    [Fact]
    public async Task ServerMode_CachesBeyondConfiguredTtl()
    {
        // 服务器说 600 秒，配置只给 100 毫秒：睡过配置 TTL 后仍然命中，证明配置值不是上限。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=600" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o =>
            {
                o.FreshnessMode = CacheFreshnessMode.Server;
                o.TimeToLive = TimeSpan.FromMilliseconds(100);
            })
        });

        await client.GetAsync(server.Url + "x");
        await Task.Delay(250);
        await client.GetAsync(server.Url + "x");

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ServerMode_HandlesAbsurdMaxAgeWithoutOverflow()
    {
        // long.MaxValue 秒会封顶到 TimeSpan.MaxValue：到期计算不能溢成「立刻过期」。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=9223372036854775807" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ServerMode_DoesNotCacheResponsesWithoutFreshnessHeaders_EndToEnd()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
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
            Cache = new AxiosCache(new AxiosCacheOptions
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
            Cache = new AxiosCache(new AxiosCacheOptions
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
            Cache = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(TimeSpan.FromSeconds(30), store.LastTimeToLive);
    }

    [Fact]
    public async Task NonGet_IsNotCached_ByDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        await client.PostAsync(server.Url + "x", new { id = 1 });
        await client.PostAsync(server.Url + "x", new { id = 1 });

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task NonSuccess_IsNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 404));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(),
            Callbacks = { ValidateStatus = _ => true }
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task StreamResponse_IsNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var first = await client.GetAsync<Stream>(server.Url + "x");
        await using (first.Data!)
        {
            Assert.NotNull(first.Data);
        }

        var second = await client.GetAsync<Stream>(server.Url + "x");
        await using (second.Data!)
        {
            Assert.NotNull(second.Data);
        }

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task HeadResponse_IsCached_WithoutABody()
    {
        // HEAD 没有正文，但头就是它的全部内容：落库后第二次命中不重发。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Array.Empty<byte>(),
            Headers: new Dictionary<string, string> { ["X-Trace"] = "head" }));

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var first = await client.HeadAsync(server.Url + "x");
        var second = await client.HeadAsync(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal("head", second.GetHeader("X-Trace"));
    }

    [Fact]
    public async Task EmptySuccessBody_IsCached()
    {
        // 204 / 空 200 也是可缓存响应：空正文同样是一份「原始字节」。
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: Array.Empty<byte>()));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var first = await client.GetAsync<string>(server.Url + "x");
        var second = await client.GetAsync<string>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal(200, second.Status);
    }

    [Fact]
    public async Task DifferentUrls_DoNotShareEntries()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

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

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

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
    public async Task CustomKeySelector_ReplacesTheDefaultKey()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.KeySelector = (_, _) => "everything")
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
            Cache = NewCache(o => o.KeySelector = (_, _) => "  ")
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
            Cache = NewCache(o => o.ShouldCache = _ => true)
        });

        // 键不含请求体：正文相同才命中（正文不同的场景必须自定义 KeySelector）。
        await client.PostAsync(server.Url + "x", new { id = 1 });
        await client.PostAsync(server.Url + "x", new { id = 1 });

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ShortCircuit_WinsOverCache_AndIsNotStored()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("real", 1)));
        var cache = NewCache();
        using var client = new AxiosClient(new AxiosConfig { Cache = cache });

        var mocked = await client.GetAsync<Person>(server.Url + "x", config => config.ShortCircuit = AxiosShortCircuit.Json("{\"name\":\"mock\",\"age\":2}"));
        var real = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("mock", mocked.Data?.Name);
        Assert.Equal("real", real.Data?.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptors_StillRunOnHit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });
        var intercepted = 0;
        client.Interceptors.Response.Use(response =>
        {
            Interlocked.Increment(ref intercepted);
            return response;
        });

        await client.GetAsync<Person>(server.Url + "x");
        await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(2, intercepted);
    }

    [Fact]
    public async Task SwappedResponse_WithoutRawBytes_IsNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        // 拦截器换了一个响应对象：新的那份没有原始字节，不能落库。
        client.Interceptors.Response.Use(response => new AxiosResponse
        {
            Status = response.Status,
            StatusText = response.StatusText,
            Data = response.Data,
            Headers = response.Headers,
            Config = response.Config
        });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("ada", first.Data!.Name);
        Assert.Equal("ada", second.Data!.Name);
    }

    [Fact]
    public async Task Clear_DropsEveryEntry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new MemoryCacheStore();
        var cache = new AxiosCache(new AxiosCacheOptions { Store = store });
        using var client = new AxiosClient(new AxiosConfig { Cache = cache });

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
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url, Cache = cache });

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
            Cache = new AxiosCache(new AxiosCacheOptions { Store = store })
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
    public async Task NoStoreRequest_DoesNotCaptureTheRawBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        // 请求自己说了 no-store：响应不可能落库，管线不该为缓存多复制一份正文。
        var noStore = await client.GetAsync<Person>(
            server.Url + "x",
            config => config.Headers.Set("Cache-Control", "no-store"));

        var plain = await client.GetAsync<Person>(server.Url + "x");

        Assert.False(noStore.Config!.CacheCaptureRawBody);
        Assert.True(plain.Config!.CacheCaptureRawBody);
    }

    private static AxiosShortCircuit Entry(string body)
        => new() { Status = 200, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes(body) };

    [Fact]
    public async Task ConcurrentRequests_SendOnce()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var tasks = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(client.GetAsync<Person>(server.Url + "x"));
        }

        // 等第一次发送真的到了服务端，再放行。
        await WaitUntilAsync(() => server.Received.Count == 1);
        await Task.Delay(100);
        release.SetResult(true);

        var responses = await Task.WhenAll(tasks);

        Assert.Single(server.Received);
        Assert.All(responses, response => Assert.Equal("ada", response.Data?.Name));

        // 只有真正发送的那一个是「有底层请求」的，其余 9 个走缓存路径。
        Assert.Equal(1, responses.Count(response => response.Request is not null));
    }

    [Fact]
    public async Task ConcurrentRequests_SendEveryTime_WhenSingleFlightDisabled()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(o => o.SingleFlight = false) });

        var tasks = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(client.GetAsync<Person>(server.Url + "x"));
        }

        await WaitUntilAsync(() => server.Received.Count == 5);
        release.SetResult(true);
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SingleFlightDisabled_StillStoresEntries()
    {
        // 关掉单飞只是「不合并并发」，落库照旧：否则这条策略下的缓存永远没有条目、命中率为零。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache(o => o.SingleFlight = false) });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal("ada", second.Data?.Name);
    }

    [Fact]
    public async Task LeaderFailure_ReachesTheWaiters()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { error = "boom" }, statusCode: 500);
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var tasks = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(client.GetAsync<Person>(server.Url + "x"));
        }

        await WaitUntilAsync(() => server.Received.Count == 1);
        await Task.Delay(100);
        release.SetResult(true);

        foreach (var task in tasks)
        {
            var error = await Assert.ThrowsAsync<AxiosException>(() => task);
            Assert.Equal(500, error.Status);
        }

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task FollowerCancellation_DoesNotCancelTheSharedRequest()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        using var cts = new CancellationTokenSource();
        var follower = client.GetAsync<Person>(server.Url + "x", config => config.CancellationToken = cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        var canceled = await Assert.ThrowsAsync<AxiosException>(() => follower);
        Assert.Equal(AxiosException.ErrCanceled, canceled.Code);

        release.SetResult(true);
        var response = await leader;

        Assert.Equal("ada", response.Data?.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task LeaderCancellation_DoesNotFailTheFollower()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        using var leaderCts = new CancellationTokenSource();
        var leader = client.GetAsync<Person>(server.Url + "x", config => config.CancellationToken = leaderCts.Token);
        await WaitUntilAsync(() => server.Received.Count == 1);

        var follower = client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(100);

        leaderCts.Cancel();
        await Assert.ThrowsAsync<AxiosException>(() => leader);

        // 领导者被它自己的调用方取消：跟随者自己发一次，而不是跟着失败。
        await WaitUntilAsync(() => server.Received.Count == 2);
        release.SetResult(true);

        var response = await follower;
        Assert.Equal("ada", response.Data?.Name);
    }

    [Fact]
    public async Task FollowerWait_IsBoundedByTotalTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        // 领导者慢，跟随者的总预算先到：不能挂在原地无限等，按统一错误模型报「总预算耗尽」。
        var follower = client.GetAsync<Person>(
            server.Url + "x",
            config => config.TotalTimeout = TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<AxiosException>(() => follower);
        Assert.True(error.IsTotalTimeoutExceeded);
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);

        release.SetResult(true);
        await leader;
    }

    [Fact]
    public async Task BoundedFollower_SendsItsOwn_WhenLeaderFailsWithCancellation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });
        using var client = new AxiosClient(new AxiosConfig { Cache = NewCache() });

        var thrown = 0;
        client.Interceptors.Response.Use(response =>
        {
            // 领导者那次响应上抛一个未经翻译的取消（用户拦截器的典型写法）。
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new OperationCanceledException();
            }

            return response;
        });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        // 跟随者有总预算：等待被领导者失败唤醒时，不能把「领导者取消」误报成本次请求的总预算耗尽。
        var follower = client.GetAsync<Person>(
            server.Url + "x",
            config => config.TotalTimeout = TimeSpan.FromSeconds(1));
        await Task.Delay(150);

        release.SetResult(true);
        await Assert.ThrowsAnyAsync<Exception>(() => leader);

        var response = await follower;
        Assert.Equal("ada", response.Data?.Name);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task DeclarativeClient_HitsTheSameCache()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var api = new ApiClient(server.Url);
        api.Axios.Defaults.Cache = NewCache();

        await api.RequestAsync(new PersonRequest());
        await api.RequestAsync(new PersonRequest());

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CustomStore_IsUsedForReadsAndWrites()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore();
        using var client = new AxiosClient(new AxiosConfig { Cache = new AxiosCache(new AxiosCacheOptions { Store = store }) });

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

        using var first = new AxiosClient(new AxiosConfig { Cache = new AxiosCache(new AxiosCacheOptions { Store = store }) });
        await first.GetAsync<Person>(server.Url + "x");

        // 第二个实例挂同一个存储：跨实例命中（Redis 这类外部存储的形态）。
        using var second = new AxiosClient(new AxiosConfig { Cache = new AxiosCache(new AxiosCacheOptions { Store = store }) });
        var hit = await second.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("ada", hit.Data?.Name);
    }

    [Fact]
    public async Task CustomStore_ReadFailure_FallsBackToSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore { OnGet = () => throw new InvalidOperationException("store down") };
        using var client = new AxiosClient(new AxiosConfig { Cache = new AxiosCache(new AxiosCacheOptions { Store = store }) });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ada", response.Data?.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CustomStore_WriteFailure_DoesNotBreakTheRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var store = new RecordingStore { OnSet = () => throw new InvalidOperationException("store down") };
        using var client = new AxiosClient(new AxiosConfig { Cache = new AxiosCache(new AxiosCacheOptions { Store = store }) });

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
            Cache = new AxiosCache(new AxiosCacheOptions
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

    [Get("person")]
    private sealed class PersonRequest : IApiRequest<Person>
    {
    }

    /// <summary>记录读写的存储：用来验证管线确实走的是接口，而不是内置字典。</summary>
    private sealed class RecordingStore : IAxiosCacheStore
    {
        private readonly MemoryCacheStore _inner = new();

        public List<string> Gets { get; } = new();

        public List<string> Sets { get; } = new();

        public List<AxiosCacheEntryPolicy> Policies { get; } = new();

        public Func<ValueTask<AxiosShortCircuit?>>? OnGet { get; set; }

        public Func<ValueTask>? OnSet { get; set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Gets.Add(key);
            return OnGet is { } onGet ? onGet() : _inner.GetAsync(key, cancellationToken);
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Sets.Add(key);
            return OnSet is { } onSet ? onSet() : _inner.SetAsync(key, entry, timeToLive, cancellationToken);
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        {
            Sets.Add(key);
            Policies.Add(policy);
            return OnSet is { } onSet ? onSet() : _inner.SetAsync(key, entry, policy.TimeToLive, cancellationToken);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => _inner.ClearAsync(cancellationToken);
    }

    /// <summary>只实现旧四方法签名的存储：验证新重载的默认实现仍然把写入接住。</summary>
    private sealed class LegacyStore : IAxiosCacheStore
    {
        private readonly MemoryCacheStore _inner = new();

        public TimeSpan LastTimeToLive { get; private set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            LastTimeToLive = timeToLive;
            return _inner.SetAsync(key, entry, timeToLive, cancellationToken);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => _inner.ClearAsync(cancellationToken);
    }

    /// <summary>写入永远不完成的存储：验证 <see cref="AxiosCacheOptions.StoreTimeout"/> 不会拖住响应。</summary>
    private sealed class HangingStore : IAxiosCacheStore
    {
        private readonly TaskCompletionSource<bool> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => new();

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
            => new(_never.Task);

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }

    private static Dictionary<string, IEnumerable<string>> Headers(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs)
        {
            headers[name] = new[] { value };
        }

        return headers;
    }

    /// <summary>一碰就抛的响应头：用来证明某条路径根本没有读取响应头。</summary>
    private sealed class ThrowingHeaders : IReadOnlyDictionary<string, IEnumerable<string>>
    {
        private static InvalidOperationException Touched() => new("响应头被读取了。");

        public IEnumerable<string> this[string key] => throw Touched();

        public IEnumerable<string> Keys => throw Touched();

        public IEnumerable<IEnumerable<string>> Values => throw Touched();

        public int Count => throw Touched();

        public bool ContainsKey(string key) => throw Touched();

        public bool TryGetValue(string key, out IEnumerable<string> value) => throw Touched();

        public IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumerator() => throw Touched();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static Dictionary<string, IEnumerable<string>> HeaderLines(params (string Name, string[] Values)[] pairs)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in pairs)
        {
            headers[name] = values;
        }

        return headers;
    }
}
