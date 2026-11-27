
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>缓存新鲜度：Cache-Control 解析、TTL / Age / Expires 计算、Vary 与请求指令。</summary>
public sealed partial class CacheTests
{
    [Fact]
    public async Task Entry_Expires_AfterTimeToLive()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(60)) });

        await client.GetAsync(server.Url + "x");
        await Task.Delay(200);
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
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
        // no-cache 只要求「复用前先验证」：查询被跳过（改走条件请求重新验证），响应仍可落库。
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

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
            Caching = new AxiosCache(new AxiosCacheOptions
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
            Caching = new AxiosCache(new AxiosCacheOptions
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
    public async Task ServerMode_CachesBeyondConfiguredTtl()
    {
        // 服务器说 600 秒，配置只给 100 毫秒：睡过配置 TTL 后仍然命中，证明配置值不是上限。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Cache-Control"] = "max-age=600" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o =>
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
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
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task CacheHit_DoesNotPartitionByVary()
    {
        // 钉住现状：内置存储与 AxiosCache 都不按 Vary 分区，命中时也不校验 Vary。
        await using var server = TestHttpServer.Start(request =>
        {
            var language = request.Headers.TryGetValue("Accept-Language", out var value) ? value : "none";
            return TestJson.Response(
                new Person(language, 1),
                headers: new Dictionary<string, string>
                {
                    ["Cache-Control"] = "max-age=600",
                    ["Vary"] = "Accept-Language"
                });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Server)
        });

        var first = await client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("Accept-Language", "zh"));
        var second = await client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("Accept-Language", "en"));

        Assert.Equal("zh", first.Data!.Name);
        Assert.Equal("zh", second.Data!.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CacheHit_AdvancesAgeHeader_BasedOnReceivedAt()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("ok"),
            Headers: new Dictionary<string, string>
            {
                ["Cache-Control"] = "max-age=30",
                ["Age"] = "3"
            }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<string>(server.Url + "aged");
        Assert.Equal("3", first.GetHeader("Age"));

        // Age 是「当前龄期」：在缓存里又放了 1.5s，命中时必须比入库时更大，而不是原样回放 3。
        await Task.Delay(1500);

        var hit = await client.GetAsync<string>(server.Url + "aged");
        Assert.Single(server.Received); // 确实是命中，而不是重新请求

        var age = int.Parse(hit.GetHeader("Age")!);
        Assert.True(age >= 4, $"Age 仍是 {age}，没有按 receivedAt 推进");
    }

}
