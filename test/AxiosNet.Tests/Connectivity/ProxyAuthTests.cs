
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Net;
using System.Text;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 代理认证：Handler 上的 <c>Credentials</c> 只用于<b>源站</b>认证，代理 407 只认 <see cref="IWebProxy.Credentials"/>。
/// 「代理地址 + 凭据」并排配置是常见用法，必须把实例级凭据补到代理上，否则每次都是 407（且被归成 4xx）。
/// </summary>
public sealed class ProxyAuthTests
{
    /// <summary>假代理：没有 <c>Proxy-Authorization</c> 就发 407 挑战，带上了就放行。</summary>
    private static Func<TestRequest, TestResponse> Challenge()
        => request => request.Headers.ContainsKey("proxy-authorization")
            ? TestJson.Response(new { ok = true })
            : new TestResponse(
                407,
                ReasonPhrase: "Proxy Authentication Required",
                Body: Array.Empty<byte>(),
                Headers: new Dictionary<string, string> { ["Proxy-Authenticate"] = "Basic realm=\"probe\"" });

    [Fact]
    public async Task InstanceCredentials_AnswerTheProxyChallenge()
    {
        await using var proxy = TestHttpServer.Start(Challenge());

        using var client = new AxiosClient(options: new AxiosClientOptions
        {
            Proxy = new WebProxy(proxy.Url) { BypassProxyOnLocal = false },
            Credentials = new NetworkCredential("user7", "pwd7"),
        });

        var response = await client.GetAsync("http://auth-target.example.invalid/");

        Assert.Equal(200, response.Status);

        // 第一条不带凭据被 407，第二条带上 Basic 凭据放行（目标主机是假域名，只经代理、不发 DNS）。
        Assert.Equal(2, proxy.Received.Count);
        Assert.False(proxy.Received[0].Headers.ContainsKey("proxy-authorization"));

        var authorization = proxy.Received[1].Headers["proxy-authorization"];
        Assert.StartsWith("Basic ", authorization, StringComparison.Ordinal);
        Assert.Equal(
            "user7:pwd7",
            Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..])));
    }

    [Fact]
    public async Task WithoutCredentials_TheProxyChallengeFailsAsBefore()
    {
        await using var proxy = TestHttpServer.Start(Challenge());

        // 没有可用的代理凭据：407 原样交给调用方，不能被误补成别的行为。
        using var client = new AxiosClient(options: new AxiosClientOptions
        {
            Proxy = new WebProxy(proxy.Url) { BypassProxyOnLocal = false },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("http://auth-target.example.invalid/"));

        Assert.Equal(407, ex.Status);
        Assert.Single(proxy.Received);
    }

    [Fact]
    public void ProxyOwnCredentials_AreNeverReplaced()
    {
        // 代理自己配了凭据时一律以它为准：不做覆盖，也不包一层。
        var proxy = new WebProxy("http://127.0.0.1:9")
        {
            Credentials = new NetworkCredential("proxy", "own"),
        };

        Assert.Same(proxy, DefaultHandlerFactory.ResolveProxy(proxy, new NetworkCredential("site", "creds")));
    }

    [Fact]
    public void NoInstanceCredentials_LeavesTheProxyUntouched()
    {
        var proxy = new WebProxy("http://127.0.0.1:9");

        Assert.Same(proxy, DefaultHandlerFactory.ResolveProxy(proxy, credentials: null));
        Assert.Null(DefaultHandlerFactory.ResolveProxy(proxy: null, new NetworkCredential("site", "creds")));
    }

    [Fact]
    public void FallbackKeepsProxyRouting()
    {
        // 包装只补凭据：绕过规则与代理地址判定必须原样转发。
        var proxy = new WebProxy("http://127.0.0.1:9") { BypassList = new[] { "^http://local\\.test" } };
        var resolved = DefaultHandlerFactory.ResolveProxy(proxy, new NetworkCredential("site", "creds"))!;

        Assert.NotSame(proxy, resolved);
        Assert.Equal(new Uri("http://127.0.0.1:9"), resolved.GetProxy(new Uri("http://auth-target.example.invalid/")));
        Assert.True(resolved.IsBypassed(new Uri("http://local.test/")));
        Assert.False(resolved.IsBypassed(new Uri("http://auth-target.example.invalid/")));
    }
}
