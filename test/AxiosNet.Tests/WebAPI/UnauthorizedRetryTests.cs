
using System.Net.Http.Json;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Resilience;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Tests.WebAPI;

/// <summary>401 重放：令牌由提供方写入时刷新并重发一次；不可重放的正文 / 显式凭据 / 匿名声明一律跳过。</summary>
public sealed class UnauthorizedRetryTests
{
    [Fact]
    public async Task FirstCall401_RefreshesAndReplaysOnce()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Replay_InvalidatesWithTheRejectedTokenValue()
    {
        await using var server = SecureServer("Bearer tok-2");

        string? staleSeen = null;
        var calls = 0;
        var provider = CredentialProviders.Create(
            (_, _) => ValueTask.FromResult(ApiCredential.Token("tok-" + Interlocked.Increment(ref calls))),
            invalidate: stale => staleSeen = stale);

        using var client = new ApiClient(server.Url) { CredentialProvider = provider };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        // staleToken 必须是头里去掉 scheme 后的令牌本体，否则管理器的「按值作废」永远不命中。
        Assert.Equal("tok-1", staleSeen);
    }

    [Fact]
    public async Task SecondCall401_Throws_NoInfiniteLoop()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var calls = 0;
        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Equal(401, ex.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Disabled_DoesNotReplay()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = tokens,
            RetryOnUnauthorized = false
        };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task NoTokenProvider_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var client = new ApiClient(server.Url) { CredentialProvider = CredentialProviders.Fixed("fixed") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SameTokenAfterRefresh_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        // 静态令牌：刷新后值不变，重放没有意义。
        using var client = new ApiClient(server.Url) { CredentialProvider = new StaticTokenProvider("same") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task AnonymousRequest_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var client = new ApiClient(server.Url) { CredentialProvider = new StaticTokenProvider("tok") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new AnonymousSecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_WithProvider_IsRejectedBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var client = new ApiClient(server.Url) { CredentialProvider = new StaticTokenProvider("tok") };

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new ExplicitAuthRequest()));

        // 显式头与凭据槽位并存是配置冲突：连一次请求都不该发，更谈不上 401 重放。
        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task NonSeekableStreamBody_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var body = new NonSeekableStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new StreamPutRequest { Payload = body }));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SeekableStreamBody_DoesNotReplay()
    {
        // 可 seek 的流属于「内核同一次请求内才能回拨」的形态：跨调用重放不做快照，因此不重发。
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new StreamPutRequest { Payload = body }));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task MultipartBody_WithFile_DoesNotReplay()
    {
        // 含流部件的 multipart 不参与跨调用重放：文件流随第一次请求一起释放，且不做快照。
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var file = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(
            new MultipartFilePutRequest { File = new FormFile("a.bin", file) }));

        Assert.Single(server.Received);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("string")]
    [InlineData("json")]
    [InlineData("form")]
    public async Task SnapshotReplayableHttpContentBody_Replays(string kind)
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var body = CreateSnapshotBody(kind);

        var response = await client.RequestAsync(new HttpContentPutRequest { Payload = body });

        Assert.Equal(200, response.Status);
        // 快照在首次发送前读取正文；重放用快照字节新建内容实例，不再碰已被释放的模板正文。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task FormDataBody_WithoutStreams_Replays()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        var response = await client.RequestAsync(new MultipartPutRequest { Name = "bob" });

        Assert.Equal(200, response.Status);
        // 无流部件的 FormData 每次发送都由模型重新装配，不需要快照也能重放。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task NonWhitelistedHttpContentBody_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var body = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new HttpContentPutRequest { Payload = body }));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Snapshot_OverMaxBytes_IsNotCreated()
    {
        using var content = new ByteArrayContent(new byte[64]);

        // 超限直接不建快照：不截断、不返回半份正文。
        Assert.Null(await RequestBodySnapshot.TryCreateAsync(content, 16, CancellationToken.None));
        Assert.NotNull(await RequestBodySnapshot.TryCreateAsync(content, 64, CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotOverMaxRequestBytes_DoesNotSnapshot()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url, defaults => defaults.Limits.MaxRequestBytes = 16)
        {
            CredentialProvider = tokens
        };
        // JsonContent 自带缓冲、ContentLength 不可预知：读取后长度才可知，随后由发送管线按上限拦下。
        using var body = JsonContent.Create(new Person(new string('x', 100), 1));

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new HttpContentPutRequest { Payload = body }));

        // 明确行为：不建快照、不截断正文；超限正文在发出前被拦下，服务端一次都没收到，自然也没有重放。
        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task IdempotencyKey_IsPreservedAcrossTheReplay()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var axios = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience =
            {
                Idempotency = { Enabled = true }
            }
            });
        using var client = new ApiClient(axios, ownsAxiosClient: false) { CredentialProvider = tokens };

        await client.RequestAsync(new SecureRequest());

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(server.Received[0].Headers["Idempotency-Key"], server.Received[1].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task InterfaceProxy_ReplaysToo()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        var response = await client.Create<ISecureApi>().GetAsync();

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task ErrorFactory_OnlySeesTheFinalFailure()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var calls = 0;
        var factoryCalls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = tokens,
            ErrorFactory = new CountingErrorFactory(() => factoryCalls++)
        };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        // 重放成功前工厂不参与：401 只被「最终失败」的那一次交给工厂。
        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task HeaderCredential_RefreshesAndReplaysOn401()
    {
        await using var server = SecureServer("Basic v2");

        var issued = 0;
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((_, _) =>
                ValueTask.FromResult(ApiCredential.Header("Basic v" + Interlocked.Increment(ref issued))))
        };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Basic v1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Basic v2", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task HeaderCredential_InvalidatesWithoutTokenValue()
    {
        await using var server = SecureServer("Basic v2");

        var issued = 0;
        string? staleSeen = "未被调用";
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create(
                (_, _) => ValueTask.FromResult(ApiCredential.Header("Basic v" + Interlocked.Increment(ref issued))),
                invalidate: stale => staleSeen = stale)
        };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        // 完整头值形态拿不到「令牌本体」，只能无条件作废 —— 传 null，不能把头里切出来的碎片当 staleToken。
        Assert.Null(staleSeen);
    }

    [Fact]
    public async Task FixedProvider_On401_ThrowsAxiosExceptionNotNullReference()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        // Fixed 走的是没给 invalidate 委托的路径：作废必须是空操作，不能 NRE。
        using var client = new ApiClient(server.Url) { CredentialProvider = CredentialProviders.Fixed("same") };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Equal(401, ex.Status);
        Assert.Single(server.Received); // 静态令牌刷新后不变，不重放
    }

    [Fact]
    public async Task Provider_With401InKernelRetryStatusCodes_DoesNotDoubleLoop()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(
            server.Url,
            defaults => defaults.Resilience.Retry.Policy = new RetryPolicy(5, TimeSpan.Zero)
            {
                RetryStatusCodes = status => status is 401
            })
        {
            CredentialProvider = tokens
        };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        // 内核在提供方槽位下不重试 401：只应出现「首次 401 + 刷新后重放一次」，不能叠成两层循环。
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NonIdempotent_WithoutAllowUnsafeRetry_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var calls = 0;
        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new PostSecureRequest()));

        // 与内核同一口径：POST 默认不重发，401 重放也不该绕过这条判定。
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task NonIdempotent_WithAllowUnsafeRetry_Replays()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(
            server.Url,
            defaults => defaults.Resilience.AllowUnsafeRetry = true)
        {
            CredentialProvider = tokens
        };

        var response = await client.RequestAsync(new PostSecureRequest());

        Assert.Equal(200, response.Status);
        // 配置级放行开关同时覆盖内核重试与这一层 401 重放：POST 也能刷新后重发一次。
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task ProviderReplay_RefreshesAndReplays()
    {
        await using var server = SecureServer("Bearer tok-2");
        var calls = 0;

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = tokens
        };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        // 提供方托管的令牌：401 后由同一来源刷新重放，映射仍走内核槽位。
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
    }

    /// <summary>内核白名单里的内存缓冲内容：都能在首次发送前被快照。</summary>
    private static HttpContent CreateSnapshotBody(string kind) => kind switch
    {
        "bytes" => new ByteArrayContent(new byte[] { 1, 2, 3 }),
        "string" => new StringContent("hi"),
        "json" => JsonContent.Create(new Person("ok", 1)),
        "form" => new FormUrlEncodedContent(new[] { KeyValuePair.Create("a", "b") }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的正文形态。")
    };

    private static TestHttpServer SecureServer(string acceptedAuthorization)
        => TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var auth) && auth == acceptedAuthorization
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

    private sealed class StaticTokenProvider(string token) : IApiCredentialProvider
    {
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(ApiCredential.Token(token));
    }

    private sealed class CountingErrorFactory(Action onCall) : IApiErrorFactory
    {
        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            onCall();
            return ValueTask.FromResult<Exception?>(null);
        }
    }
}

[Post("secure")]
public sealed class PostSecureRequest : IApiRequest<Person>
{
}

[Get("secure")]
public sealed class SecureRequest : IApiRequest<Person>
{
    [Header("X-Trace")] public string? Trace { get; set; }
}

[AllowAnonymous]
[Get("secure")]
public sealed class AnonymousSecureRequest : IApiRequest<Person>
{
}

[Headers("Authorization: Basic dXNlcjpwYXNz")]
[Get("secure")]
public sealed class ExplicitAuthRequest : IApiRequest<Person>
{
}

[Put("secure")]
public sealed class HttpContentPutRequest : IApiRequest<Person>
{
    [Body] public HttpContent? Payload { get; set; }
}

[Put("secure")]
public sealed class StreamPutRequest : IApiRequest<Person>
{
    [Body] public Stream? Payload { get; set; }
}

[Multipart]
[Put("secure")]
public sealed class MultipartPutRequest : IApiRequest<Person>
{
    [Part] public string? Name { get; set; }
}

[Multipart]
[Put("secure")]
public sealed class MultipartFilePutRequest : IApiRequest<Person>
{
    [Part] public FormFile? File { get; set; }
}

public interface ISecureApi
{
    [Get("secure")]
    Task<AxiosResponse<Person>> GetAsync(CancellationToken cancellationToken = default);
}
