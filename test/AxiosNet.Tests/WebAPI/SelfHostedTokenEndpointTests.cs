using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

/// <summary>取令牌与刷新令牌复用同一个 ApiClient：匿名声明让令牌端点不会重新进入授权链路。</summary>
public sealed class SelfHostedTokenEndpointTests
{
    [Fact]
    public async Task AcquireRefreshAndReplay_AllThroughTheSameClient()
    {
        var issued = 0;
        await using var server = TestHttpServer.Start(request =>
        {
            if (request.Path == "/token")
            {
                var token = "tok-" + Interlocked.Increment(ref issued);
                return TestJson.Response(new { token, expiresIn = 300 });
            }

            return request.Headers.TryGetValue("Authorization", out var auth) && auth == "Bearer tok-2"
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401);
        });

        ApiClient? client = null;
        using var tokens = new AccessTokenManager(new AccessTokenOptions(async ct =>
        {
            var result = await client!.RequestAsync(new AcquireTokenRequest(), cancellationToken: ct);
            return ToAccessToken(result);
        })
        {
            RefreshAsync = async ct =>
            {
                var result = await client!.RequestAsync(new RefreshTokenRequest(), cancellationToken: ct);
                return ToAccessToken(result);
            }
        });

        client = new ApiClient(server.Url) { CredentialProvider = tokens };
        using var api = client!;

        var first = await api.RequestAsync(new SelfHostedProtectedRequest());
        var second = await api.RequestAsync(new SelfHostedProtectedRequest());

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, issued);

        var received = server.Received;
        Assert.Equal(5, received.Count);
        Assert.Equal("/token", received[0].Path);
        Assert.Equal("/secure", received[1].Path);
        Assert.Equal("/token", received[2].Path);
        Assert.Equal("/secure", received[3].Path);
        Assert.Equal("/secure", received[4].Path);
        Assert.False(received[0].Headers.ContainsKey("Authorization"));
        Assert.False(received[2].Headers.ContainsKey("Authorization"));
        Assert.Equal("Bearer tok-1", received[1].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", received[3].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", received[4].Headers["Authorization"]);
    }

    /// <summary>把令牌端点的响应转成管理器需要的令牌与过期时间。</summary>
    private static AccessToken ToAccessToken(AxiosResponse<SelfHostedToken> response)
    {
        var body = response.Data!;
        return new AccessToken(body.Token, DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn));
    }
}

/// <summary>匿名令牌端点：获取。</summary>
[Get("token")]
[AllowAnonymous]
public sealed class AcquireTokenRequest : IApiRequest<SelfHostedToken>
{
}

/// <summary>匿名令牌端点：刷新。</summary>
[Get("token")]
[AllowAnonymous]
public sealed class RefreshTokenRequest : IApiRequest<SelfHostedToken>
{
}

/// <summary>受保护端点。</summary>
[Get("secure")]
public sealed class SelfHostedProtectedRequest : IApiRequest<Person>
{
}

/// <summary>令牌端点返回的令牌与有效期（秒）。</summary>
public sealed record SelfHostedToken(string Token, int ExpiresIn);
