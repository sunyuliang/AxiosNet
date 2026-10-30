using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Resilience;
using AxiosNet.WebAPI;

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
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };

        var response = await client.RequestAsync(new SecureRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SecondCall401_Throws_NoInfiniteLoop()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var calls = 0;
        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref calls)))));
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };

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
            AccessTokenProvider = tokens,
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

        using var client = new ApiClient(server.Url) { Authorization = new BearerAuthorizationProvider("fixed") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SameTokenAfterRefresh_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        // 静态令牌：刷新后值不变，重放没有意义。
        using var client = new ApiClient(server.Url) { AccessTokenProvider = new StaticTokenProvider("same") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task AnonymousRequest_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var client = new ApiClient(server.Url) { AccessTokenProvider = new StaticTokenProvider("tok") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new AnonymousSecureRequest()));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var client = new ApiClient(server.Url) { AccessTokenProvider = new StaticTokenProvider("tok") };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new ExplicitAuthRequest()));

        var received = Assert.Single(server.Received);
        Assert.Equal("Basic dXNlcjpwYXNz", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task NonSeekableStreamBody_DoesNotReplay()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };
        using var body = new NonSeekableStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new StreamBodyRequest { Payload = body }));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SeekableStreamBody_DoesNotReplay()
    {
        // 可 seek 也没用：正文随第一次请求的消息一起被释放，重放只会拿到已释放的流。
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ => ValueTask.FromResult(new AccessToken("tok"))));
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new StreamBodyRequest { Payload = body }));

        Assert.Single(server.Received);
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
            Idempotency = { Enabled = true }
        });
        using var client = new ApiClient(axios, ownsAxiosClient: false) { AccessTokenProvider = tokens };

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
        using var client = new ApiClient(server.Url) { AccessTokenProvider = tokens };

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
            AccessTokenProvider = tokens,
            ErrorFactory = new CountingErrorFactory(() => factoryCalls++)
        };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SecureRequest()));

        // 重放成功前工厂不参与：401 只被「最终失败」的那一次交给工厂。
        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, server.Received.Count);
    }

    private static TestHttpServer SecureServer(string acceptedAuthorization)
        => TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var auth) && auth == acceptedAuthorization
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

    private sealed class StaticTokenProvider(string token) : IAccessTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(token);
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

[Post("upload")]
public sealed class StreamBodyRequest : IApiRequest<Person>
{
    [Body] public Stream? Payload { get; set; }
}

public interface ISecureApi
{
    [Get("secure")]
    Task<AxiosResponse<Person>> GetAsync(CancellationToken cancellationToken = default);
}
