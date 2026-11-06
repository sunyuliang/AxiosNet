using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using AxiosNet.Cache;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.WebAPI;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 第 1 批对抗性测试：懒 holder、连接默认回写、单槽位凭据、401 快照重放、可重放性矩阵。
/// 断言按「故意注入一个错误实现就会变红」的强度写，不做抽样。
/// </summary>
public sealed class Batch1AdversarialTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // ────────────────────────────── 1. 懒 holder ──────────────────────────────

    [Fact]
    public void FreshConfig_ReadsNeverAllocateLazyHolders()
    {
        var config = new AxiosConfig();

        Assert.Null(Holder(config, "_concurrencyState"));
        Assert.Null(Holder(config, "_cacheState"));
        Assert.Null(Holder(config, "_authReplay"));

        // 所有 ?? null 读取点逐个叶子读一遍，都不能把 holder 建出来。
        Assert.Null(config.ShortCircuit);
        Assert.Null(config.Caching);
        Assert.False(config.CacheCaptureRawBody);
        Assert.Null(config.RevalidationEntry);
        Assert.Null(config.ResolvedAuthorization);
        Assert.Null(config.InternalCredentialProvider);
        Assert.False(config.RetainBodyContent);
        Assert.Null(config.RetainedReplayContent);

        var concurrency = config.Resilience.Concurrency;
        Assert.Null(concurrency.MaxInFlight);
        Assert.Null(concurrency.MaxQueue);
        Assert.Null(concurrency.QueueTimeout);

        _ = config.Serialization; // 只创建分组本身，不创建值格式化状态
        Assert.Null(config.Serialization.ValueText);
        Assert.Null(config.Serialization.QueryEncoder);
        Assert.Null(config.Serialization.FormFieldName);

        Assert.Null(Holder(config, "_concurrencyState"));
        Assert.Null(Holder(config, "_cacheState"));
        Assert.Null(Holder(config, "_authReplay"));
        Assert.Null(ValueTextHolder(config));
    }

    [Fact]
    public void CloneOfDefaultConfig_AllocatesNoLazyHolder()
    {
        var clone = new AxiosConfig().Clone();

        Assert.Null(Holder(clone, "_concurrencyState"));
        Assert.Null(Holder(clone, "_cacheState"));
        Assert.Null(Holder(clone, "_authReplay"));
        Assert.Null(ValueTextHolder(clone));
    }

    [Fact]
    public void WritingDefaultLeaves_DoesNotLeaveOrphanHolders()
    {
        var config = new AxiosConfig();

        config.ShortCircuit = null;
        config.Caching = null;
        config.CacheCaptureRawBody = false;
        config.RevalidationEntry = null;
        config.ResolvedAuthorization = null;
        config.InternalCredentialProvider = null;
        config.RetainBodyContent = false;
        config.RetainedReplayContent = null;
        config.Serialization.QueryEncoder = null;
        config.Serialization.FormFieldName = null;

        Assert.Null(Holder(config, "_cacheState"));
        Assert.Null(Holder(config, "_authReplay"));
        Assert.Null(ValueTextHolder(config));
    }

    [Fact]
    public void WritingNullConcurrency_IsAnExplicitSetting_SoTheHolderExists()
    {
        var config = new AxiosConfig();

        config.Resilience.Concurrency.MaxInFlight = null;

        // 显式 null 代表「关闭实例默认值」，是有语义的写入，必须留下 *Set 标记。
        Assert.NotNull(Holder(config, "_concurrencyState"));
        Assert.Null(config.Resilience.Concurrency.MaxInFlight);
        Assert.NotNull(config.ResilienceOrNull);
    }

    [Theory]
    [InlineData("_concurrencyState")]
    [InlineData("_cacheState")]
    [InlineData("_authReplay")]
    public void DefaultHolder_IsLeafEquivalentToNoHolder(string field)
    {
        var plain = new AxiosConfig();
        var installed = new AxiosConfig();
        InstallDefaultHolder(installed, field);

        Assert.NotNull(Holder(installed, field));
        Assert.Equal(LeafVector(plain), LeafVector(installed));
    }

    [Fact]
    public void DefaultValueTextHolder_IsLeafEquivalentToNoHolder()
    {
        var plain = new AxiosConfig();
        var installed = new AxiosConfig();
        InstallDefaultValueTextHolder(installed);

        Assert.NotNull(ValueTextHolder(installed));
        Assert.Equal(LeafVector(plain), LeafVector(installed));
    }

    [Fact]
    public void Clone_CopiesHoldersAndIsolatesTheirLeaves()
    {
        var source = new AxiosConfig();
        source.Resilience.Concurrency.MaxInFlight = 3;
        source.Resilience.Concurrency.MaxQueue = 2;
        source.Resilience.Concurrency.QueueTimeout = TimeSpan.FromSeconds(1);
        source.Serialization.QueryEncoder = _ => "a=1";
        source.Serialization.FormFieldName = (prefix, key) => key;

        var clone = source.Clone();

        Assert.Equal(3, clone.Resilience.Concurrency.MaxInFlight);
        Assert.Equal(2, clone.Resilience.Concurrency.MaxQueue);
        Assert.Equal(TimeSpan.FromSeconds(1), clone.Resilience.Concurrency.QueueTimeout);
        Assert.NotNull(clone.Serialization.QueryEncoder);
        Assert.NotNull(clone.Serialization.FormFieldName);
        Assert.NotEqual(ValueTextHolder(source), ValueTextHolder(clone));

        clone.Resilience.Concurrency.MaxInFlight = 9;
        clone.Serialization.QueryEncoder = _ => "b=2";

        Assert.Equal(3, source.Resilience.Concurrency.MaxInFlight);
        Assert.Equal("a=1", source.Serialization.QueryEncoder!(null));
    }

    [Fact]
    public void Clone_ClearsRequestScopedLeaves_KeepsInstanceScopedOnes()
    {
        var source = new AxiosConfig();
        source.ShortCircuit = new AxiosShortCircuit { Status = 299 };
        source.Caching = new AxiosCache();
        source.CacheCaptureRawBody = true;
        source.RevalidationEntry = new AxiosShortCircuit { Status = 304 };
        source.RetainBodyContent = true;
        source.RetainedReplayContent = new ByteArrayContent(new byte[] { 1 });
        source.ResolvedAuthorization = "Bearer tok";
        source.InternalCredentialProvider = new NullCredentialProvider();
        source.Transport.Auth = AxiosCredential.Bearer("tok");

        var clone = source.Clone();

        Assert.Null(clone.ShortCircuit);
        Assert.False(clone.CacheCaptureRawBody);
        Assert.Null(clone.RevalidationEntry);
        Assert.Same(source.Caching, clone.Caching);

        Assert.False(clone.RetainBodyContent);
        Assert.Null(clone.RetainedReplayContent);
        Assert.Null(clone.InternalCredentialProvider);

        // 保留解析结果：拦截器克隆配置后不该让同一槽位再解析一次。
        Assert.Equal("Bearer tok", clone.ResolvedAuthorization);
        Assert.Same(source.TransportOrNull!.Auth, clone.TransportOrNull!.Auth);
        Assert.False(clone.TransportOrNull!.AuthSet);

        // holder 按引用隔离：改克隆影响不到来源。
        clone.ResolvedAuthorization = "Bearer other";
        clone.RetainedReplayContent = new ByteArrayContent(new byte[] { 2 });
        Assert.Equal("Bearer tok", source.ResolvedAuthorization);
        Assert.NotNull(source.RetainedReplayContent);
    }

    [Fact]
    public void ConcurrencyResetFrom_NoSourceState_ClearsHolder()
    {
        var target = new AxiosConfig();
        target.Resilience.Concurrency.MaxInFlight = 4;
        target.Resilience.Concurrency.MaxQueue = 1;
        target.Resilience.Concurrency.QueueTimeout = TimeSpan.FromSeconds(2);

        target.Resilience.Concurrency.ResetFrom(new AxiosConfig());

        Assert.Null(Holder(target, "_concurrencyState"));
        Assert.Null(target.Resilience.Concurrency.MaxInFlight);
        Assert.Null(target.Resilience.Concurrency.MaxQueue);
        Assert.Null(target.Resilience.Concurrency.QueueTimeout);
        Assert.Null(target.ResilienceOrNull);
    }

    [Fact]
    public void ConcurrencyResetFrom_CopiesValuesAndSetFlags()
    {
        var source = new AxiosConfig();
        source.Resilience.Concurrency.MaxInFlight = 7;
        source.Resilience.Concurrency.MaxQueue = null;

        var target = new AxiosConfig();
        target.Resilience.Concurrency.ResetFrom(source);

        Assert.Equal(7, target.Resilience.Concurrency.MaxInFlight);
        Assert.Null(target.Resilience.Concurrency.MaxQueue);
        Assert.NotNull(target.ResilienceOrNull);
    }

    [Fact]
    public void CloneOfEffectiveConfig_DoesNotCarryRequestScopedReplayState()
    {
        // 模拟管线里写进去的请求级状态，再按「库产出的配置」克隆：回放时不得复用上一轮的正文。
        var effective = new AxiosConfig();
        effective.EffectiveMerged = true;
        effective.RetainBodyContent = true;
        effective.RetainedReplayContent = new ByteArrayContent(new byte[] { 1 });
        effective.ResolvedAuthorization = "Bearer tok";

        var clone = effective.Clone();

        Assert.False(clone.EffectiveMerged);
        Assert.True(clone.InstanceOptionsInherited);
        Assert.False(clone.RetainBodyContent);
        Assert.Null(clone.RetainedReplayContent);
        Assert.Equal("Bearer tok", clone.ResolvedAuthorization);
    }

    // ─────────────────────── 2. SyncConnectionDefaults 条件回写 ───────────────────────

    [Fact]
    public void DefaultPool_DoesNotCreateTransportGroup_AndGettersMatchPool()
    {
        using var client = new AxiosClient(new AxiosConfig());

        // 不回写路径：默认值本来就和池一致，不能再往 Defaults 上挂一个空/满的 Transport 组。
        Assert.Null(client.Defaults.TransportOrNull);
        AssertConnectionDefaultsMatchPool(client);
    }

    [Fact]
    public void ExplicitDefaultsRedirects_AreWrittenBackAndFlagsCleared()
    {
        var defaults = new AxiosConfig();
        defaults.Transport.MaxRedirects = 2;

        using var client = new AxiosClient(defaults);

        Assert.False(client.Defaults.MaxRedirectsSet);
        Assert.Equal(2, client.Defaults.TransportOrNull!.MaxRedirects);
        AssertConnectionDefaultsMatchPool(client);
        Assert.Equal(2, client.Pool!.EffectiveOptions.MaxAutomaticRedirections);
    }

    [Fact]
    public void PoolOptionsUseCookies_WritesBackWithCredentialsAndCookieStore()
    {
        using var client = new AxiosClient(
            new AxiosConfig(),
            poolOptions: new ConnectionPoolOptions { UseCookies = true, MaxAutomaticRedirections = 7 });

        Assert.True(client.Defaults.Transport.WithCredentials);
        Assert.False(client.Defaults.TransportOrNull!.WithCredentialsSet);
        Assert.NotNull(client.Pool!.Cookies);
        AssertConnectionDefaultsMatchPool(client);
    }

    [Fact]
    public void PoolOptionsProxyAndCredentials_AreWrittenBackByReference()
    {
        var proxy = new FixedProxy();
        var credentials = new NetworkCredential("user", "pass");
        var options = new ConnectionPoolOptions { Proxy = proxy, Credentials = credentials };

        using var client = new AxiosClient(new AxiosConfig(), poolOptions: options);

        Assert.Same(proxy, client.Defaults.TransportOrNull!.Proxy);
        Assert.Same(credentials, client.Defaults.TransportOrNull!.Credentials);
        AssertConnectionDefaultsMatchPool(client);
    }

    [Fact]
    public void PoolOptionsNoAutoRedirect_GetterIsZero()
    {
        using var client = new AxiosClient(
            new AxiosConfig(),
            poolOptions: new ConnectionPoolOptions { AllowAutoRedirect = false, MaxAutomaticRedirections = 9 });

        Assert.Equal(0, client.Defaults.TransportOrNull!.MaxRedirects);
        AssertConnectionDefaultsMatchPool(client);
    }

    [Fact]
    public void SharedPool_WritesBackItsEffectiveValues()
    {
        using var pool = new HttpConnectionPool(
            new ConnectionPoolOptions { UseCookies = true, MaxAutomaticRedirections = 6 });
        using var client = new AxiosClient(new AxiosConfig(), pool);

        Assert.Equal(6, client.Defaults.TransportOrNull!.MaxRedirects);
        Assert.True(client.Defaults.TransportOrNull!.WithCredentials);
        AssertConnectionDefaultsMatchPool(client);
    }

    [Fact]
    public void CustomHandler_NeverCreatesTransportGroup()
    {
        using var client = new AxiosClient(new AxiosConfig(), handler: new HttpClientHandler());

        Assert.Null(client.Pool);
        Assert.Null(client.Defaults.TransportOrNull);
    }

    [Fact]
    public async Task RecitingEffectiveConnectionValuesPerRequest_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { UseCookies = true, MaxAutomaticRedirections = 4 });

        var response = await client.GetAsync(
            "secure",
            c =>
            {
                c.Transport.MaxRedirects = 4;
                c.Transport.WithCredentials = true;
            });

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ConflictingConnectionValuePerRequest_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { UseCookies = true, MaxAutomaticRedirections = 4 });

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "secure",
            c => c.Transport.MaxRedirects = 5));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    // ─────────────────────────── 3. 单槽位凭据 Transport.Auth ───────────────────────────

    [Theory]
    [InlineData("basic")]
    [InlineData("bearer")]
    [InlineData("header")]
    [InlineData("provider")]
    public async Task StaticCredentialForms_AllWriteTheSameAuthorizationField(string form)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var provider = new CountingCoreProvider(AxiosCredential.Bearer("tok-provider"));

        var defaults = new AxiosConfig { BaseAddress = server.Url };
        defaults.Transport.Auth = form switch
        {
            "basic" => AxiosCredential.Basic("user", "pass"),
            "bearer" => AxiosCredential.Bearer("tok"),
            "header" => AxiosCredential.Header("Digest abc"),
            "provider" => AxiosCredential.Provider(provider),
            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };

        using var client = new AxiosClient(defaults);
        var response = await client.GetAsync("secure");

        Assert.Equal(200, response.Status);
        var expected = form switch
        {
            "basic" => TestJson.BasicAuth("user", "pass"),
            "bearer" => "Bearer tok",
            "header" => "Digest abc",
            _ => "Bearer tok-provider",
        };
        Assert.Equal(expected, server.Received[0].Headers["authorization"]);
        Assert.Equal(form == "provider" ? 1 : 0, provider.Calls);
    }

    [Fact]
    public async Task PerRequestAuthNull_SuppressesInstanceDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var defaults = new AxiosConfig { BaseAddress = server.Url };
        defaults.Transport.Auth = AxiosCredential.Bearer("instance");

        using var client = new AxiosClient(defaults);

        // 快捷方法路径：生效配置直接从实例默认值克隆，显式 null 直接生效。
        await client.GetAsync("secure", c => c.Transport.Auth = null);

        // 通用路径：普通配置要经过 MergeConfig 的槽位合并，显式 null 必须能压住实例默认值。
        await client.RequestAsync(new AxiosConfig
        {
            Url = "secure",
            Transport = { Auth = null },
        });

        Assert.Equal(2, server.Received.Count);
        Assert.False(server.Received[0].Headers.ContainsKey("authorization"));
        Assert.False(server.Received[1].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_PlusStaticAuthSlot_IsRejectedBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var defaults = new AxiosConfig { BaseAddress = server.Url };
        defaults.Transport.Auth = AxiosCredential.Bearer("instance");

        using var client = new AxiosClient(defaults);
        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "secure",
            c => c.Headers.Set("Authorization", "Basic dXNlcjpwYXNz")));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ProviderReturningNone_DoesNotWriteHeaderAndDoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        var provider = new CountingApiProvider(_ => ApiCredential.None);

        using var client = new ApiClient(server.Url) { CredentialProvider = provider };
        var error = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Equal(401, error.Status);
        Assert.Single(server.Received);
        Assert.False(server.Received[0].Headers.ContainsKey("authorization"));
        Assert.Equal(1, provider.GetCalls);
    }

    [Fact]
    public async Task ReplayCall_DoesNotResolveProviderAgain()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var auth) && auth == "Bearer tok-2"
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        var provider = new CountingApiProvider(CountingApiProvider.Rotate("tok"));

        using var client = new ApiClient(server.Url) { CredentialProvider = provider };
        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        // 首次解析一次 + 401 刷新一次；重放那次只复用静态槽位，不能再调用提供方。
        Assert.Equal(2, provider.GetCalls);
        Assert.Equal(1, provider.InvalidateCalls);
    }

    [Fact]
    public async Task CacheIdentity_UsesResolvedCredentialNotTheDefaultSlot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var defaults = new AxiosConfig { BaseAddress = server.Url, Caching = new AxiosCache() };
        using var client = new AxiosClient(defaults);

        await client.GetAsync("secure", c => c.Transport.Auth = AxiosCredential.Bearer("a"));
        await client.GetAsync("secure", c => c.Transport.Auth = AxiosCredential.Bearer("a"));
        Assert.Single(server.Received);

        // 换个身份必须是另一个缓存键；否则会把 a 的响应交给 b。
        await client.GetAsync("secure", c => c.Transport.Auth = AxiosCredential.Bearer("b"));
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer a", server.Received[0].Headers["authorization"]);
        Assert.Equal("Bearer b", server.Received[1].Headers["authorization"]);
    }

    // ─────────────────────────── 4. 401 快照重放 RequestBodySnapshot ───────────────────────────

    [Fact]
    public async Task ConcurrentUnauthorized_EachReplayKeepsItsOwnBody()
    {
        await using var server = TestHttpServer.Start(request =>
        {
            var auth = request.Headers.TryGetValue("authorization", out var value) ? value : null;
            return auth is not null && auth.StartsWith("Bearer new-", StringComparison.Ordinal)
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401);
        });

        var provider = new PerRequestTokenProvider();
        using var client = new ApiClient(
            server.Url,
            defaults => defaults.Resilience.AllowUnsafeRetry = true)
        {
            CredentialProvider = provider,
        };

        var first = new Batch1ConcurrentPostRequest { Id = "A", Payload = new Batch1Payload("A", "alpha") };
        var second = new Batch1ConcurrentPostRequest { Id = "B", Payload = new Batch1Payload("B", "beta") };

        var responses = await Task.WhenAll(
            client.RequestAsync(first),
            client.RequestAsync(second));

        Assert.Equal(200, responses[0].Status);
        Assert.Equal(200, responses[1].Status);

        var received = server.Received;
        Assert.Equal(4, received.Count);

        foreach (var id in new[] { "A", "B" })
        {
            var calls = received.Where(r => r.Headers["x-id"] == id).ToList();
            Assert.Equal(2, calls.Count);
            // 重放必须用这一路自己的正文，不能串到并发那一路。
            Assert.Equal(calls[0].Body, calls[1].Body);
            Assert.Equal($"Bearer old-{id}", calls[0].Headers["authorization"]);
            Assert.Equal($"Bearer new-{id}", calls[1].Headers["authorization"]);
        }

        // 两条逻辑请求各自「首次解析 + 刷新解析」，重放那一发不再调提供方。
        Assert.Equal(4, provider.GetCalls);
        Assert.Equal(2, provider.InvalidateCalls);
    }

    [Fact]
    public async Task SnapshotReadFailure_FallsBackToTheOriginal401()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        var provider = new CountingApiProvider((_, n) => ApiCredential.Token("tok-" + n));

        using var client = new ApiClient(server.Url) { CredentialProvider = provider };
        var body = new FailsOnSecondReadContent(new byte[] { 1, 2, 3 });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new HttpContentPutRequest { Payload = body }));

        Assert.Equal(401, error.Status);
        // 快照读失败不能变成第二个请求，也不能把 IO 异常漏出去。
        Assert.Single(server.Received);
        Assert.Equal(2, provider.GetCalls);
    }

    [Fact]
    public async Task Replay_DisposesTheRetainedContentExactlyOnce()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var auth) && auth == "Bearer tok-2"
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        var provider = new CountingApiProvider(CountingApiProvider.Rotate("tok"));

        using var client = new ApiClient(server.Url) { CredentialProvider = provider };
        var body = new DisposeCountingContent(new byte[] { 9, 8, 7 });

        var response = await client.RequestAsync(new HttpContentPutRequest { Payload = body });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(server.Received[0].Body, server.Received[1].Body);
        // 首次发送后正文仍在手里（快照要读它），结束时只释放一次；二次释放会在这里暴露。
        Assert.Equal(1, body.DisposeCount);
    }

    // ─────────────────────────── 5. 可重放性矩阵 BodyReplayability ───────────────────────────

    [Theory]
    [InlineData("null", "Rebuild", true, true)]
    [InlineData("bytes", "Rebuild", true, true)]
    [InlineData("string", "Rebuild", true, true)]
    [InlineData("object", "Rebuild", true, true)]
    [InlineData("multipart-fields", "Rebuild", false, false)]
    [InlineData("multipart-stream", "Streamed", true, false)]
    [InlineData("seekable-stream", "Streamed", true, false)]
    [InlineData("nonseekable-stream", "None", false, false)]
    [InlineData("bytearray-content", "Content", true, true)]
    [InlineData("memory-content", "Content", true, true)]
    [InlineData("string-content", "Content", true, true)]
    [InlineData("form-content", "Content", true, true)]
    [InlineData("json-content", "Content", true, false)]
    [InlineData("stream-content", "None", false, false)]
    public void ReplayabilityMatrix_SequentialVersusHedge(
        string shape,
        string expectedKind,
        bool sequential,
        bool hedge)
    {
        var config = CreateBodyConfig(shape);

        Assert.Equal(expectedKind, BodyReplayability.Classify(config).ToString());
        Assert.Equal(sequential, BodyReplayability.CanReplay(config, ReplayMode.Sequential));
        Assert.Equal(hedge, BodyReplayability.CanReplay(config, ReplayMode.Hedge));
    }

    // ─────────────────────────────── 辅助 ───────────────────────────────

    private static AxiosConfig CreateBodyConfig(string shape)
    {
        var config = new AxiosConfig();
        switch (shape)
        {
            case "null":
                break;
            case "bytes":
                config.Body = new byte[] { 1, 2, 3 };
                break;
            case "string":
                config.Body = "hi";
                break;
            case "object":
                config.Body = new Person("ok", 1);
                break;
            case "multipart-fields":
                config.ContentType = AxiosContentType.MultipartFormData;
                config.Body = new FormData { ["name"] = "value" };
                break;
            case "multipart-stream":
                config.ContentType = AxiosContentType.MultipartFormData;
                config.Body = new FormData { ["file"] = new MemoryStream(new byte[] { 1, 2 }) };
                break;
            case "seekable-stream":
                config.Body = new MemoryStream(new byte[] { 1, 2 });
                break;
            case "nonseekable-stream":
                config.Body = new NonSeekableStream();
                break;
            case "bytearray-content":
                config.Body = new ByteArrayContent(new byte[] { 1 });
                break;
            case "memory-content":
                config.Body = new ReadOnlyMemoryContent(new byte[] { 1 });
                break;
            case "string-content":
                config.Body = new StringContent("hi");
                break;
            case "form-content":
                config.Body = new FormUrlEncodedContent(new[] { KeyValuePair.Create("a", "b") });
                break;
            case "json-content":
                config.Body = JsonContent.Create(new Person("ok", 1));
                break;
            case "stream-content":
                config.Body = new StreamContent(new MemoryStream(new byte[] { 1 }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "未知的正文形态。");
        }

        return config;
    }

    private static object? Holder(AxiosConfig config, string field)
        => typeof(AxiosConfig).GetField(field, PrivateInstance)!.GetValue(config);

    private static object? ValueTextHolder(AxiosConfig config)
        => typeof(AxiosConfig.SerializationSettings)
            .GetField("_valueTextState", PrivateInstance)!
            .GetValue(config.Serialization);

    private static void InstallDefaultHolder(AxiosConfig config, string field)
    {
        var info = typeof(AxiosConfig).GetField(field, PrivateInstance)!;
        info.SetValue(config, Activator.CreateInstance(info.FieldType, nonPublic: true));
    }

    private static void InstallDefaultValueTextHolder(AxiosConfig config)
    {
        var info = typeof(AxiosConfig.SerializationSettings).GetField("_valueTextState", PrivateInstance)!;
        info.SetValue(config.Serialization, Activator.CreateInstance(info.FieldType, nonPublic: true));
    }

    private static string LeafVector(AxiosConfig config)
        => string.Join('|', new[]
        {
            config.ShortCircuit?.Status.ToString() ?? "-",
            config.Caching is null ? "-" : "cache",
            config.CacheCaptureRawBody.ToString(),
            config.RevalidationEntry?.Status.ToString() ?? "-",
            config.ResolvedAuthorization ?? "-",
            config.InternalCredentialProvider is null ? "-" : "provider",
            config.RetainBodyContent.ToString(),
            config.RetainedReplayContent is null ? "-" : "content",
            config.ResilienceOrNull is null ? "-" : "resilience",
            config.Resilience.Concurrency.MaxInFlight?.ToString() ?? "-",
            config.Resilience.Concurrency.MaxQueue?.ToString() ?? "-",
            config.Resilience.Concurrency.QueueTimeout?.ToString() ?? "-",
            config.Serialization.ValueText is null ? "-" : "valueText",
            config.Serialization.QueryEncoder is null ? "-" : "query",
            config.Serialization.FormFieldName is null ? "-" : "form",
            config.Serialization.ValueTextSet.ToString(),
        });

    private static void AssertConnectionDefaultsMatchPool(AxiosClient client)
    {
        var options = client.Pool!.EffectiveOptions;
        var transport = client.Defaults.TransportOrNull;

        var expectedMaxRedirects = options.AllowAutoRedirect ? options.MaxAutomaticRedirections : 0;
        Assert.Equal(expectedMaxRedirects, transport?.MaxRedirects ?? 5);
        Assert.Equal(options.UseCookies, transport?.WithCredentials ?? false);
        Assert.Equal(options.Proxy, transport?.Proxy);
        Assert.Equal(options.Credentials, transport?.Credentials);
    }

    private sealed class CountingCoreProvider(AxiosCredential? credential) : IAxiosCredentialProvider
    {
        public int Calls { get; private set; }

        public bool CanRefresh => true;

        public ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(credential);
        }

        public void Invalidate(string? staleValue)
        {
        }
    }

    private sealed class NullCredentialProvider : IAxiosCredentialProvider
    {
        public bool CanRefresh => false;

        public ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<AxiosCredential?>(null);
    }

    private sealed class CountingApiProvider : IApiCredentialProvider
    {
        private readonly Func<ApiAuthContext, int, ApiCredential> _factory;
        private int _calls;

        public CountingApiProvider(Func<ApiAuthContext, ApiCredential> factory)
            : this((context, _) => factory(context))
        {
        }

        public CountingApiProvider(Func<ApiAuthContext, int, ApiCredential> factory) => _factory = factory;

        public int GetCalls => Volatile.Read(ref _calls);

        public int InvalidateCalls { get; private set; }

        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(_factory(context, call));
        }

        public void Invalidate(string? staleToken = null) => InvalidateCalls++;

        public static Func<ApiAuthContext, int, ApiCredential> Rotate(string prefix)
            => (_, call) => ApiCredential.Token(prefix + "-" + call);
    }

    private sealed class PerRequestTokenProvider : IApiCredentialProvider
    {
        private readonly ConcurrentDictionary<string, string> _fresh = new(StringComparer.Ordinal);
        private int _calls;
        private int _invalidates;

        public int GetCalls => Volatile.Read(ref _calls);

        public int InvalidateCalls => Volatile.Read(ref _invalidates);

        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var id = ((Batch1ConcurrentPostRequest)context.Request).Id!;
            var token = _fresh.TryGetValue(id, out var fresh) ? fresh : "old-" + id;
            return ValueTask.FromResult(ApiCredential.Token(token));
        }

        public void Invalidate(string? staleToken = null)
        {
            Interlocked.Increment(ref _invalidates);
            if (staleToken is not null && staleToken.StartsWith("old-", StringComparison.Ordinal))
            {
                _fresh[staleToken[4..]] = "new-" + staleToken[4..];
            }
        }
    }

    private sealed class FixedProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => destination;

        public bool IsBypassed(Uri host) => true;
    }

    private sealed class FailsOnSecondReadContent : ByteArrayContent
    {
        private int _serializations;

        public FailsOnSecondReadContent(byte[] bytes) : base(bytes)
        {
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (Interlocked.Increment(ref _serializations) > 1)
            {
                throw new IOException("快照读取失败（注入）。");
            }

            await base.SerializeToStreamAsync(stream, context);
        }
    }

    private sealed class DisposeCountingContent : ByteArrayContent
    {
        private int _disposeCount;

        public DisposeCountingContent(byte[] bytes) : base(bytes)
        {
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

[Post("secure")]
public sealed class Batch1ConcurrentPostRequest : IApiRequest<Person>
{
    [Header("X-Id")] public string? Id { get; set; }

    [Body] public Batch1Payload? Payload { get; set; }
}

public sealed record Batch1Payload(string Id, string Data);
