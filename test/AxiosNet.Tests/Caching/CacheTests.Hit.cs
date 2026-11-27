
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>缓存命中的交付形态：状态码 / 头回放、正文所有权、拦截器与短路优先级。</summary>
public sealed partial class CacheTests
{
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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal(201, hit.Status);
        Assert.Equal("Created", hit.StatusText);
        Assert.Equal("abc", hit.GetHeader("X-Trace"));
        Assert.Equal("application/json; charset=utf-8", hit.GetHeader("Content-Type"));
    }

    [Fact]
    public async Task RangeRequests_AreNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("sid=secret; Path=/", first.GetHeader("Set-Cookie"));

        // 会话 cookie 属于「这一次连接」：命中回放等于把别人的会话塞进下一次请求。
        Assert.Null(hit.GetHeader("Set-Cookie"));
    }

    [Fact]
    public async Task SetCookie2_IsNotReplayedOnHit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("ada", 36),
            headers: new Dictionary<string, string> { ["Set-Cookie2"] = "sid=secret; Path=/" }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var hit = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.Equal("sid=secret; Path=/", first.GetHeader("Set-Cookie2"));
        Assert.Null(hit.GetHeader("Set-Cookie2"));
    }

    [Fact]
    public async Task NonGet_IsNotCached_ByDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
            Caching = NewCache(),
            ValidateStatus = _ => true
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task StreamResponse_IsNotCached()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<string>(server.Url + "x");
        var second = await client.GetAsync<string>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal(200, second.Status);
    }

    [Fact]
    public async Task ShortCircuit_WinsOverCache_AndIsNotStored()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("real", 1)));
        var cache = NewCache();
        using var client = new AxiosClient(new AxiosConfig { Caching = cache });

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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });
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
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

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
    public async Task NoStoreRequest_DoesNotCaptureTheRawBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(o => o.FreshnessMode = CacheFreshnessMode.Intersection)
        });

        // 请求自己说了 no-store：响应不可能落库，管线不该为缓存多复制一份正文。
        var noStore = await client.GetAsync<Person>(
            server.Url + "x",
            config => config.Headers.Set("Cache-Control", "no-store"));

        var plain = await client.GetAsync<Person>(server.Url + "x");

        Assert.False(noStore.Config!.CacheCaptureRawBody);
        Assert.True(plain.Config!.CacheCaptureRawBody);
    }

    [Fact]
    public async Task CacheHit_ReplaysAllValuesOfADuplicateHeader()
    {
        // WWW-Authenticate 允许一行里给出多个 challenge，HttpClient 会拆成多个值；
        // 折叠成首值会改变语义，缓存必须原样保留全部值（Set-Cookie 仍然单独丢弃）。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("ok"),
            Headers: new Dictionary<string, string>
            {
                ["Cache-Control"] = "max-age=30",
                ["WWW-Authenticate"] = "Basic realm=\"a\", Bearer realm=\"b\""
            }));

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var first = await client.GetAsync<string>(server.Url + "multi");
        var networkValues = first.Headers["WWW-Authenticate"].ToList();
        Assert.Equal(2, networkValues.Count);

        var hit = await client.GetAsync<string>(server.Url + "multi");
        Assert.Single(server.Received);
        Assert.Equal(networkValues, hit.Headers["WWW-Authenticate"].ToList());
    }

    [Fact]
    public async Task DeclarativeClient_HitsTheSameCache()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var api = new ApiClient(server.Url);
        api.Axios.Defaults.Caching = NewCache();

        await api.RequestFullAsync(new PersonRequest());
        await api.RequestFullAsync(new PersonRequest());

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task JsonResponse_EntrySharesBody()
    {
        var store = new RecordingCacheStore();
        var payload = TestJson.Bytes(new Person("ada", 36));
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload));

        byte[]? observed = null;
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        await client.GetAsync<Person>("person", c => c.Observability.OnRawResponse = (_, body) =>
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(body, out var segment))
            {
                observed = segment.Array;
            }
        });

        // JSON 形态的 Data 是另建的对象，条目与响应共用同一块内存（OnRawResponse 拿到的是只在回调期间有效的借用视图，不算持有）。
        Assert.NotNull(observed);
        Assert.Same(observed, store.LastEntry!.Body);
    }

    [Fact]
    public async Task ByteArrayResponse_EntryKeepsItsOwnCopy()
    {
        var store = new RecordingCacheStore();
        var payload = TestJson.Bytes(new Person("ada", 36));
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        var response = await client.GetAsync<byte[]>("blob");

        // byte[] 形态的 Data 就是原始字节本身：条目必须自持一份，否则调用方一次改写就污染后续命中。
        Assert.NotSame(response.Data, store.LastEntry!.Body);
        Assert.Equal(payload, store.LastEntry.Body);

        var before = store.LastEntry.Body![0];
        response.Data![0] = (byte)(before ^ 0xFF);
        Assert.Equal(before, store.LastEntry.Body[0]);
    }

    [Fact]
    public async Task TransformWrappingRawBytes_EntryKeepsItsOwnCopy()
    {
        var store = new RecordingCacheStore();
        var payload = TestJson.Bytes(new Person("ada", 36));
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Caching = new AxiosCache(new AxiosCacheOptions { Store = store })
        });

        // transformResponse 把原始字节包进自己的对象：Data 不再是那块数组，但内部仍引用着它。
        var response = await client.GetAsync<byte[][]>("blob", c =>
        {
            c.ResponseType = ResponseType.ArrayBuffer;
            c.Serialization.TransformResponse.Add((data, _) => new[] { (byte[])data! });
        });

        // 无法证明调用方没拿到引用 → 按最坏情况克隆，改写 Data 不会污染条目。
        Assert.NotSame(store.LastEntry!.Body, response.Data![0]);

        var before = store.LastEntry.Body![0];
        response.Data[0][0] = (byte)(before ^ 0xFF);
        Assert.Equal(before, store.LastEntry.Body[0]);
    }

}
