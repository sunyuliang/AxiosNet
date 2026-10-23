using System.Net;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-21 审计 P0 三项的回归护栏（证明见 <c>.workbuddy/reports/2026-10-21-p0-code-proof.md</c>）：
/// <list type="bullet">
/// <item>P0-1：<c>RequestAsync(config)</c> 路径上，按请求写实例级选项必须抛 <c>ERR_BAD_OPTION_VALUE</c>
/// （与快捷方法一致，不能静默丢弃）；</item>
/// <item>P0-2：回放 <c>AxiosResponse.Config</c> 必须走「合并到本实例默认值」的常规路径，
/// 不得因 <c>EffectiveMerged</c> 泄漏而整段跳过合并；</item>
/// <item>P0-3：<c>ContentType</c> / 请求头取值不得放行 CR/LF 等控制字符（HTTP 头注入）。</item>
/// </list>
/// </summary>
public sealed class Audit20261021P0FixTests
{
    private static TestResponse Json(string body = "{\"ok\":true}") => new(
        Body: Encoding.UTF8.GetBytes(body),
        ContentType: "application/json; charset=utf-8");

    // ------------------------------------------------------------------
    // P0-1：实例级选项不再被静默丢弃
    // ------------------------------------------------------------------

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
        // 写与实例相同的值等价于不写：不能因为「写了」就报错。
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

    // ------------------------------------------------------------------
    // P0-2：回放 response.Config 仍然合并本实例默认值
    // ------------------------------------------------------------------

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

        // 源实例把 maxRedirects 定为 3（实例级，进的是连接池 Handler）
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });
        var first = await source.GetAsync("x");

        // 目标实例用默认值 5：回放不是「按请求写 MaxRedirects」，不得报 ERR_BAD_OPTION_VALUE，
        // 且实例级选项应归目标实例（3 不该被当成按请求项带过来）。
        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var replayed = await target.RequestAsync(first.Config!);

        Assert.Equal(200, replayed.Status);
        Assert.Equal(5, replayed.Config!.Connection.MaxRedirects);
    }

    // ------------------------------------------------------------------
    // P0-3：控制字符不得进入请求头
    // ------------------------------------------------------------------

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
        // 端到端：即便绕过 setter 直接改字段的路径不存在，这里确认「配置阶段就拒绝」，
        // 请求根本发不出去 —— 服务端拿不到注入头。
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.PostAsync(
                "x",
                new { a = 1 },
                c => c.ContentType = "application/json\r\nX-Injected: yes"));

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
        // HTAB 是合法的 field-content，不应被连带禁掉。
        var headers = new AxiosHeaders();
        headers.Set("X-Test", "a\tb");
        Assert.Equal("a\tb", headers.Get("X-Test"));
    }
}
