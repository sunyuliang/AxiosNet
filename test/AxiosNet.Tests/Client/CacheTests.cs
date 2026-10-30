using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.Client;

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
    public async Task Clear_DropsEveryEntry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        var cache = NewCache();
        using var client = new AxiosClient(new AxiosConfig { Cache = cache });

        await client.GetAsync(server.Url + "x");
        Assert.Equal(1, cache.Count);

        cache.Clear();

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
        Assert.True(cache.Remove(cache.KeyFor(config, new Uri(server.Url + "x"))));

        await client.GetAsync("x");
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task MaxEntries_StopsAcceptingNewEntries()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Response(new { path = request.Path }));
        var cache = NewCache(o => o.MaxEntries = 1);
        using var client = new AxiosClient(new AxiosConfig { Cache = cache });

        await client.GetAsync(server.Url + "a");
        await client.GetAsync(server.Url + "b");

        Assert.Equal(1, cache.Count);

        // 满了不收新条目：b 再取还是发请求，a 仍然命中。
        await client.GetAsync(server.Url + "b");
        await client.GetAsync(server.Url + "a");

        Assert.Equal(3, server.Received.Count);
    }

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
    public async Task Options_RejectNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxiosCache(new AxiosCacheOptions { MaxEntries = 0 }));
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
}
