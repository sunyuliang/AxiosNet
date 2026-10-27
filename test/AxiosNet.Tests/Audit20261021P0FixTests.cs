using System.Net;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>按请求写实例级选项必须抛 <c>ERR_BAD_OPTION_VALUE</c>；回放 <c>AxiosResponse.Config</c> 仍合并本实例默认值；请求头与 ContentType 不得放行控制字符。</summary>
public sealed class Audit20261021P0FixTests
{
    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    [Fact]
    public async Task RequestAsync_PerRequestMaxRedirects_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    MaxRedirects = 0,
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestWithCredentials_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    WithCredentials = true,
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestProxy_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    Proxy = new WebProxy("http://127.0.0.1:1"),
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestPoolOptions_Throws()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig
            {
                Url = "x",
                Connection = new()
                {
                    PoolOptions = new ConnectionPoolOptions { MaxConnectionsPerServer = 1 },
                },
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
    }

    [Fact]
    public async Task RequestAsync_PerRequestMaxRedirects_SameAsInstance_IsAllowed()
    {
        // 写与实例相同的值等价于不写。
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = "x",
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task ReplayResponseConfig_AppliesTargetInstanceDefaultHeaders()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var first = await source.GetAsync("x");

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        target.Defaults.Headers.Set("X-Instance", "target");

        await target.RequestAsync(first.Config!);

        var replayed = server.Received[1];
        Assert.Equal("target", replayed.Headers["X-Instance"]);
    }

    [Fact]
    public async Task ReplayResponseConfig_RunsTargetInstanceTransformChain()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var first = await source.GetAsync("x");

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        target.Defaults.TransformResponse.Add((_, _) => "unwrapped-by-target");

        var replayed = await target.RequestAsync(first.Config!);

        Assert.Equal("unwrapped-by-target", replayed.Data);
    }

    [Fact]
    public async Task ReplayResponseConfig_DoesNotFalselyRejectInstanceLevelOptions()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        // 源实例把 maxRedirects 定为 3，进的是连接池 Handler。
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });
        var first = await source.GetAsync("x");

        // 回放不是「按请求写 MaxRedirects」，实例级选项应归目标实例。
        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var replayed = await target.RequestAsync(first.Config!);

        Assert.Equal(200, replayed.Status);
        Assert.Equal(5, replayed.Config!.Connection.MaxRedirects);
    }

    [Theory]
    [InlineData("application/json\r\nX-Injected: yes")]
    [InlineData("application/json\nX-Injected: yes")]
    [InlineData("application/json\rX-Injected: yes")]
    public void ContentType_WithCrlf_Throws(string contentType)
    {
        Assert.Throws<ArgumentException>(() => new AxiosConfig { ContentType = contentType });
    }

    [Fact]
    public async Task ContentType_WithCrlf_CannotReachTheWire()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await Assert.ThrowsAsync<ArgumentException>(async () => await client.PostAsync("x", new { a = 1 }, c => c.ContentType = "application/json\r\nX-Injected: yes"));

        Assert.Empty(server.Received);
    }

    [Theory]
    [InlineData("a\u000bb")]  // VT
    [InlineData("a\fb")]      // FF
    [InlineData("a\u0001b")]  // SOH
    [InlineData("a\u007fb")]  // DEL
    public void Headers_ControlCharsInValue_Throw(string value)
    {
        var headers = new AxiosHeaders();
        Assert.Throws<ArgumentException>(() => headers.Set("X-Test", value));
    }

    [Fact]
    public void Headers_TabInValue_IsAllowed()
    {
        // HTAB 是合法的 field-content。
        var headers = new AxiosHeaders();
        headers.Set("X-Test", "a\tb");
        Assert.Equal("a\tb", headers.Get("X-Test"));
    }
}
