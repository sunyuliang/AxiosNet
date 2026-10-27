
using AxiosNet.Tests.Infrastructure;
using System.Text;

namespace AxiosNet.Tests.Client;

/// <summary>
/// Basic 认证与 Bearer token（核心层）：编码、覆盖顺序、非法凭据。
/// <para>
/// 声明式层（<c>ApiClient</c> + <c>BearerAuthorizationProvider</c>）的授权语义归
/// <c>WebAPI/AuthorizationTests</c>——那边 <c>Bearer_StaticToken_SetsAuthorizationHeader</c> 与
/// <c>Bearer_TokenWithScheme_IsSentAsIs</c> 就是本文件原先那条「带不带 scheme 都照发」的用例，且拆得更细。
/// </para>
/// </summary>
public sealed class AuthTests
{
    // ------------------------------------------------------------------
    // Basic 认证：user-id 不允许冒号（RFC 7617 用第一个冒号做分隔）
    // ------------------------------------------------------------------

    [Fact]
    public void AxiosAuth_UsernameWithColon_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AxiosAuth("dom\\user:admin", "pwd"));

        var auth = new AxiosAuth();
        Assert.Throws<ArgumentException>(() => auth.Username = "a:b");
    }

    [Fact]
    public async Task AxiosAuth_PasswordMayContainColon_AndHeaderIsCorrect()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 用 "/" 显式请求 baseAddress 本身（空串 / null 现在按非法地址拒绝）
        await client.GetAsync("/", c => c.Connection.Auth = new AxiosAuth("user", "p@ss:word"));

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss:word"));
        Assert.Equal(expected, server.Received[0].Headers["Authorization"]);
    }

    // AxiosAuth：Basic 编造边界

    [Fact]
    public async Task Auth_PasswordWithColon_IsNotSplit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("user", "pass:word"));

        Assert.Equal(TestJson.BasicAuth("user", "pass:word"), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_UnicodeCredentials_RoundTripThroughUtf8()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("用户名", "密码"));

        var header = server.Received[0].Headers["authorization"];
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("用户名:密码")), header);
        Assert.Equal("用户名:密码", Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
    }

    [Fact]
    public async Task Auth_EmptyPassword_StillSendsHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("user", string.Empty));

        Assert.Equal(TestJson.BasicAuth("user", string.Empty), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_PerRequest_OverridesInstanceDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                Auth = new AxiosAuth("instance", "secret"),
            },
        });

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("percall", "other"));

        Assert.Equal(TestJson.BasicAuth("percall", "other"), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_ExplicitAuthorizationHeader_WinsOverAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c =>
        {
            c.Connection.Auth = new AxiosAuth("user", "secret");
            c.Headers.Set("Authorization", "Bearer token");
        });

        Assert.Equal("Bearer token", server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_ConfigSendsBasicAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "secure", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        Assert.Equal(TestJson.BasicAuth("user", "pass"), server.Received[0].Headers.GetValueOrDefault("authorization"));
    }
}
