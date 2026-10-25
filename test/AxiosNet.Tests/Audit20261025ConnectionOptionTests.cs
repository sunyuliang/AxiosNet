using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 按请求校验 <c>WithCredentials</c> 的口径：必须比对<b>连接池的生效值</b>
/// （<see cref="ConnectionPoolOptions.UseCookies"/>），而不是 <c>Defaults.WithCredentials</c>。
/// <para>
/// 连接级选项在构造时固化进池，构造后再改 <c>Defaults</c> 不生效（文档承诺「静默不生效、不报错」）。
/// 旧实现读 <c>Defaults</c> 当生效值，于是构造后改过 defaults 的实例上：
/// 按请求<b>复述池的真实值</b>被拒、报错文案声称一个从未生效的值是「实例生效值」。
/// </para>
/// <para>
/// 另一半是 <c>WithCredentialsSet</c> 早退：合并产物清掉了继承来的标记，不早退会让
/// 「构造后改过 Defaults」的实例在每个普通请求上都被判成写入了不同的值（直接违反上面那条承诺）。
/// </para>
/// </summary>
public sealed class Audit20261025ConnectionOptionTests
{
    // ------------------------------------------------------------------
    // 构造后改 Defaults：校验以池为准
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_RepeatsPoolValue_AfterDefaultsMutated_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 构造后改 defaults：不生效，池仍是 UseCookies = false
        client.Defaults.Connection.WithCredentials = true;
        Assert.False(client.Pool!.Options.UseCookies);

        // 按请求复述池的真实值 false —— 必须放行（旧实现拿 Defaults 的 true 比对，误抛）
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_RepeatsMutatedDefaults_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Defaults.Connection.WithCredentials = true; // 未生效

        // 按请求写 true 等于「要求打开 Cookie 容器」，但池没开 —— 必须响亮报错
        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);

        // 文案报的「实例生效值」必须是池的真实值（False），不是 Defaults 上的 True
        Assert.Contains("实例生效值为 False", error.Message);
    }

    [Fact]
    public async Task PerRequestWithCredentials_Omitted_AfterDefaultsMutated_DoesNotThrow()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Defaults.Connection.WithCredentials = true;

        // 构造后改 defaults 的承诺是「静默不生效」，不是「每个请求都炸」
        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // poolOptions 打开 Cookie：Defaults 上看不出来，只有池知道
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_MatchesPoolOptionsUseCookies_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { UseCookies = true });

        Assert.True(client.Pool!.Options.UseCookies);

        // defaults 没写 WithCredentials（读出来是 false），但池的生效值是 true
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_DiffersFromPoolOptionsUseCookies_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { UseCookies = true });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
        Assert.Contains("实例生效值为 True", error.Message);
    }

    // ------------------------------------------------------------------
    // 共享池：Cookie 容器由池决定，与本实例 defaults 无关
    // ------------------------------------------------------------------

    [Fact]
    public async Task SharedPool_PerRequestWithCredentials_RepeatsPoolValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { UseCookies = true });
        using var client = new AxiosClient(pool: pool);

        // 共享池场景 defaults 上不能写连接级选项（构造期就抛），所以只能复述池的值
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SharedPool_PerRequestWithCredentials_DiffersFromPoolValue_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();
        using var client = new AxiosClient(pool: pool);

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 回归：构造期设置照旧生效（口径改成池值后不能把这条弄丢）
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestWithCredentials_EqualToConstructionTimeValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new() { WithCredentials = true },
        });

        Assert.True(client.Pool!.Options.UseCookies);

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = true);

        Assert.Equal(200, response.Status);
    }
}
