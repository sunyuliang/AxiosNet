using System.Net;
using System.Text;
using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 缓存条件请求：过期条目带校验器时发 If-None-Match / If-Modified-Since，304 直接交付库里的正文。
/// </summary>
public sealed class CacheConditionalTests
{
    private static AxiosCache NewCache(Action<AxiosCacheOptions>? configure = null)
    {
        var options = new AxiosCacheOptions();
        configure?.Invoke(options);
        return new AxiosCache(options);
    }

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
    public async Task ExpiredEntry_IsRevalidatedWithIfNoneMatch_And304ServesTheStoredBody()
    {
        var revalidated = 0;
        await using var server = TestHttpServer.Start(request =>
        {
            if (request.Headers.TryGetValue("If-None-Match", out var etag) && etag == "\"v1\"")
            {
                Interlocked.Increment(ref revalidated);
                return new TestResponse(304, Headers: Headers(("ETag", "\"v1\""), ("X-Revalidated", "yes")));
            }

            return TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\"")));
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50))
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);

        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("\"v1\"", server.Received[1].Headers["If-None-Match"]);

        // 304 没有正文：状态码与正文都取缓存里的那一份，头按 304 更新。
        Assert.Equal(200, second.Status);
        Assert.Equal("ada", second.Data!.Name);
        Assert.Equal("yes", second.GetHeader("X-Revalidated"));
        Assert.Equal("\"v1\"", second.GetHeader("ETag"));

        // 条件请求确实发出去了（与「命中不发请求」不同）。
        Assert.NotNull(second.Request);
    }

    [Fact]
    public async Task LastModified_IsUsedWhenTheEntryHasNoETag()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("If-Modified-Since", out var since)
                && since == "Wed, 21 Oct 2026 07:28:00 GMT"
                ? new TestResponse(304)
                : TestJson.Response(
                    new Person("ada", 36),
                    headers: Headers(("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT"))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50))
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.False(server.Received[1].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("ada", second.Data!.Name);
    }

    [Fact]
    public async Task NotModified_RefreshesTheStoredEntry_SoTheNextRequestHitsWithoutSending()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304, Headers: Headers(("ETag", "\"v1\"")))
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50))
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);

        var revalidated = await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        // 304 之后的落库把存活时长续上了：第三次直接命中，不再发请求。
        Assert.Equal(2, server.Received.Count);
        Assert.NotNull(revalidated.Request);
        Assert.Null(hit.Request);
        Assert.Equal("ada", hit.Data!.Name);
    }

    [Fact]
    public async Task CallerConditionalHeaders_AreLeftAlone_AndThe304StaysAnError()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("If-None-Match", out var etag) && etag == "\"caller\""
                ? new TestResponse(304)
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50))
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);

        // 调用方自己写了条件头：库里不覆盖它，也不接管这次 304（没有正文可合并）。
        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(
            server.Url + "x",
            config => config.Headers.Set("If-None-Match", "\"caller\"")));

        Assert.Equal(304, error.Status);
        Assert.Equal("\"caller\"", server.Received[1].Headers["If-None-Match"]);
    }

    [Fact]
    public async Task ConditionalRequestsDisabled_SendsUnconditionally()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304)
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o =>
            {
                o.TimeToLive = TimeSpan.FromMilliseconds(50);
                o.ConditionalRequests = false;
            })
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);
        await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.False(server.Received[1].Headers.ContainsKey("If-None-Match"));
    }

    [Fact]
    public async Task RequestNoCache_RevalidatesTheStoredEntryInsteadOfServingItBlindly()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304)
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        await client.GetAsync<Person>(server.Url + "x");

        // no-cache：不能直接复用已落库的条目，但可以拿它发条件请求（RFC 9111 §5.2.1.4）。
        var response = await client.GetAsync<Person>(
            server.Url + "x",
            config => config.Headers.Set("Cache-Control", "no-cache"));

        Assert.Equal(2, server.Received.Count);
        Assert.True(server.Received[1].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("ada", response.Data!.Name);
        Assert.NotNull(response.Request);
    }

    [Fact]
    public async Task RequestMaxAgeZero_RevalidatesWithoutStoringAgain()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304)
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        await client.GetAsync<Person>(server.Url + "x");

        // max-age=0：必须重新取；条件请求命中 304 时仍交付缓存的正文。
        var response = await client.GetAsync<Person>(
            server.Url + "x",
            config => config.Headers.Set("Cache-Control", "max-age=0"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("ada", response.Data!.Name);

        // max-age=0 不落库：原始字节不该为缓存多留一份。
        Assert.False(response.Config!.CacheCaptureRawBody);
    }

    [Fact]
    public async Task ExpiredEntries_WithValidators_AreKeptForRevalidation()
    {
        var store = new MemoryCacheStore();

        await store.SetAsync(
            "etag",
            new AxiosShortCircuit
            {
                Body = Encoding.UTF8.GetBytes("hello"),
                Headers = Headers(("ETag", "\"v1\""))
            },
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        await store.SetAsync(
            "plain",
            new AxiosShortCircuit { Body = Encoding.UTF8.GetBytes("hello") },
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        await Task.Delay(50);

        // 两者都已过期：普通条目读到时顺手摘掉，带校验器的留给条件请求。
        Assert.Null(await store.GetAsync("etag", CancellationToken.None));
        Assert.Null(await store.GetAsync("plain", CancellationToken.None));

        Assert.NotNull(await store.GetRevalidationAsync("etag", CancellationToken.None));
        Assert.Null(await store.GetRevalidationAsync("plain", CancellationToken.None));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task StoreWithoutRevalidationSupport_SendsUnconditionally()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304)
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        // 只实现 IAxiosCacheStore 的存储拿不出旧条目：退化成每次完整下载，请求本身不受影响。
        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions
            {
                Store = new PlainStore(),
                TimeToLive = TimeSpan.FromMilliseconds(50)
            })
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
        Assert.False(server.Received[1].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("ada", second.Data!.Name);
    }

    [Fact]
    public async Task ConditionalRequest_DoesNotLeaveTheStoredEntryOnTheReturnedConfig()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.ContainsKey("If-None-Match")
                ? new TestResponse(304, Headers: Headers(("ETag", "\"v1\"")))
                : TestJson.Response(new Person("ada", 36), headers: Headers(("ETag", "\"v1\""))));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50))
        });

        await client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(150);
        var revalidated = await client.GetAsync<Person>(server.Url + "x");

        // 旧条目只属于那一次发送：留在响应配置上会拖着整段缓存正文，回放配置还会带上过时的条件头。
        Assert.Null(revalidated.Config!.RevalidationEntry);
        Assert.Equal("ada", revalidated.Data!.Name);
    }

    [Fact]
    public async Task MultipleSetCookieHeaders_On304_AreExposedSeparately()
    {
        using var client = new AxiosClient(
            new AxiosConfig { Cache = NewCache(o => o.TimeToLive = TimeSpan.FromMilliseconds(50)) },
            handler: new MultiSetCookieHandler());

        await client.GetAsync<Person>("https://probe.example/x");
        await Task.Delay(150);
        var second = await client.GetAsync<Person>("https://probe.example/x");

        // Set-Cookie 不能按逗号折叠：两条必须各自成条，否则客户端解析出的 Cookie 是坏的。
        Assert.Equal(200, second.Status);
        Assert.Equal("ada", second.Data!.Name);
        Assert.Equal(
            new[] { "a=1; Path=/", "b=2; Path=/" },
            second.Headers["Set-Cookie"].ToArray());
    }

    /// <summary>第一次回 200 + ETag，之后回 304 并带两条 Set-Cookie。</summary>
    private sealed class MultiSetCookieHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(call == 1 ? HttpStatusCode.OK : HttpStatusCode.NotModified);
            response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");

            if (call == 1)
            {
                response.Content = new StringContent("{\"name\":\"ada\"}", Encoding.UTF8, "application/json");
            }
            else
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", "a=1; Path=/");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "b=2; Path=/");
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>只实现基础接口的存储：没有条件请求所需的旧条目。</summary>
    private sealed class PlainStore : IAxiosCacheStore
    {
        private readonly MemoryCacheStore _inner = new();

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => _inner.SetAsync(key, entry, timeToLive, cancellationToken);

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask ClearAsync(CancellationToken cancellationToken)
            => _inner.ClearAsync(cancellationToken);
    }
}
