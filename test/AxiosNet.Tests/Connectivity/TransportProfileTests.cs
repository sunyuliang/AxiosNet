using System.Collections.Concurrent;
using System.Net;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 命名传输档：同一条池里按请求选出口（代理 / 凭据 / 客户端证书 / Cookie 会话），
/// 每个档在同一 origin 上有独立的 Handler、连接与 Cookie 容器；未登记的档必须显式报错而不是静默回落默认档。
/// </summary>
public sealed class TransportProfileTests
{
    [Fact]
    public async Task Transport_EachProfileGetsItsOwnHandlerAndNode()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var seen = new ConcurrentQueue<string>();
        var proxyA = new FakeProxy();

        var options = new ConnectionPoolOptions()
            .AddTransport("a", new ConnectionTransportOptions
            {
                Proxy = proxyA,
                HandlerFactory = ctx =>
                {
                    // 工厂必须看到本档的档名与出口设置（不是默认档的）。
                    seen.Enqueue($"{ctx.Profile}|{ReferenceEquals(proxyA, ctx.Transport.Proxy)}|{ctx.CookieContainer is null}");
                    return ctx.CreateDefaultHandler();
                }
            })
            .AddTransport("b", new ConnectionTransportOptions
            {
                HandlerFactory = ctx =>
                {
                    seen.Enqueue($"{ctx.Profile}|{ctx.Transport.Proxy is null}");
                    return ctx.CreateDefaultHandler();
                }
            });

        using var pool = new HttpConnectionPool(options);
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url + "x", c => c.Connection.Transport = "a");
        await client.GetAsync(server.Url + "x", c => c.Connection.Transport = "b");

        Assert.Equal(new[] { "a|True|True", "b|True" }, seen.ToArray());

        // 同一 origin 两个档 = 两个节点，各自持有自己的 Handler。
        var stats = pool.GetStatistics();
        Assert.Equal(2, stats.EndpointCount);
        Assert.Equal(new[] { "a", "b" }, stats.Endpoints.Select(e => e.Transport).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Transport_UnknownProfileFailsBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var exception = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("x", c => c.Connection.Transport = "missing"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
        Assert.Contains("missing", exception.Message);

        // 静默回落默认档会把「以为走了专线」变成安全事故：必须一个字节都不发。
        Assert.Empty(server.Received);
    }

    [Fact]
    public void Transport_CookieContainersAreIsolatedPerProfile()
    {
        var options = new ConnectionPoolOptions { UseCookies = true }
            .AddTransport("a", new ConnectionTransportOptions { UseCookies = true })
            .AddTransport("b", new ConnectionTransportOptions { UseCookies = false })
            .AddTransport("c", new ConnectionTransportOptions { UseCookies = true });

        using var pool = new HttpConnectionPool(options);

        Assert.NotNull(pool.Cookies);
        Assert.NotNull(pool.GetCookies("a"));
        Assert.Null(pool.GetCookies("b"));
        Assert.NotNull(pool.GetCookies("c"));

        // 每个档一份容器：默认档与命名档、命名档之间都不共享会话。
        Assert.NotSame(pool.Cookies, pool.GetCookies("a"));
        Assert.NotSame(pool.GetCookies("a"), pool.GetCookies("c"));

        // 登出换新容器，所有档一起换。
        var beforeDefault = pool.Cookies;
        var beforeA = pool.GetCookies("a");
        pool.ClearCookies();
        Assert.NotSame(beforeDefault, pool.Cookies);
        Assert.NotSame(beforeA, pool.GetCookies("a"));
    }

    [Fact]
    public void AddTransport_RejectsEmptyOrDuplicateNames()
    {
        var options = new ConnectionPoolOptions();

        Assert.Throws<ArgumentException>(() => options.AddTransport("   ", new ConnectionTransportOptions()));

        options.AddTransport("a", new ConnectionTransportOptions());

        var duplicate = Assert.Throws<ArgumentException>(() => options.AddTransport("a", new ConnectionTransportOptions()));
        Assert.Contains("已登记", duplicate.Message);
    }

    [Fact]
    public async Task Transport_IsRejectedWithInjectedHandler()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(handler: new HttpClientHandler());

        var exception = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "x", c => c.Connection.Transport = "a"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void NamedPool_RejectsDifferentTransportProfiles()
    {
        var key = "transport-" + Guid.NewGuid().ToString("N");

        try
        {
            HttpConnectionPool.GetOrCreate(
                key,
                new ConnectionPoolOptions().AddTransport("a", new ConnectionTransportOptions()));

            var exception = Assert.Throws<AxiosException>(() => HttpConnectionPool.GetOrCreate(
                key,
                new ConnectionPoolOptions().AddTransport("b", new ConnectionTransportOptions())));

            Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
        }
        finally
        {
            HttpConnectionPool.Release(key);
        }
    }

    /// <summary>只用于验证代理实例被正确带到对应档的工厂上下文里，不会真的发请求。</summary>
    private sealed class FakeProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => null;

        public bool IsBypassed(Uri host) => true;
    }
}
