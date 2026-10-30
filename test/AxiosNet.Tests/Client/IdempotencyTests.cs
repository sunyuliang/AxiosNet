using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Client;

/// <summary>幂等键：一个逻辑请求一个键，重试 / 换端点 / 对冲的各次发送共用同一个键。</summary>
public sealed class IdempotencyTests
{
    private const string Header = "Idempotency-Key";

    [Fact]
    public async Task Disabled_ByDefault_WritesNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x");

        Assert.False(server.Received[0].Headers.ContainsKey(Header));
    }

    [Fact]
    public async Task Enabled_WritesHeader_AndEachRequestGetsItsOwnKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Idempotency = { Enabled = true } });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        var first = server.Received[0].Headers[Header];
        var second = server.Received[1].Headers[Header];

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(32, first.Length); // Guid 的 N 格式
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Retry_ReusesTheSameKey()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(3, TimeSpan.Zero) },
            Idempotency = { Enabled = true }
        });

        await client.GetAsync(server.Url + "x");

        Assert.Equal(3, server.Received.Count);

        var key = server.Received[0].Headers[Header];
        Assert.All(server.Received, request => Assert.Equal(key, request.Headers[Header]));
    }

    [Fact]
    public async Task Failover_ReusesTheSameKey()
    {
        await using var primary = TestHttpServer.Start(_ => new TestResponse(500, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = { Endpoints = new[] { new Uri(mirror.Url) } },
            Idempotency = { Enabled = true }
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Equal(primary.Received[0].Headers[Header], mirror.Received[0].Headers[Header]);
    }

    [Fact]
    public async Task Hedging_ReusesTheSameKey()
    {
        var arrivals = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            var arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
            }

            return TestJson.Response(new { branch = arrival });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = { MaxHedges = 1, Delay = TimeSpan.FromMilliseconds(50) },
            Idempotency = { Enabled = true }
        });

        await client.GetAsync("x");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(server.Received[0].Headers[Header], server.Received[1].Headers[Header]);
    }

    [Fact]
    public async Task ExplicitKey_Wins_AndIsTrimmed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c =>
        {
            c.Idempotency.Enabled = true;
            c.Idempotency.Key = "  order-42  ";
        });

        Assert.Equal("order-42", server.Received[0].Headers[Header]);
    }

    [Fact]
    public async Task ExplicitBlankKey_Throws_AndSendsNothing()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Idempotency = { Enabled = true, Key = " " } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task KeyFactory_Blank_Throws_AndSendsNothing()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Idempotency = { Enabled = true, KeyFactory = _ => "" }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task KeyFactory_SeesTheEffectiveConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Idempotency = { Enabled = true, KeyFactory = config => "key:" + config.Method }
        });

        await client.PostAsync(server.Url + "x", new { id = 1 });

        Assert.Equal("key:POST", server.Received[0].Headers[Header]);
    }

    [Fact]
    public async Task CustomHeaderName_IsUsed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Idempotency = { Enabled = true, HeaderName = "X-Request-Id" }
        });

        await client.GetAsync(server.Url + "x");

        Assert.True(server.Received[0].Headers.ContainsKey("X-Request-Id"));
        Assert.False(server.Received[0].Headers.ContainsKey(Header));
    }

    [Fact]
    public async Task ExplicitHeader_IsNotOverridden()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Idempotency = { Enabled = true } });

        await client.GetAsync(server.Url + "x", c => c.Headers.Set(Header, "mine"));

        Assert.Equal("mine", server.Received[0].Headers[Header]);
    }

    [Fact]
    public async Task InvalidHeaderName_ThrowsAxiosException_AndSendsNothing()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Idempotency = { Enabled = true, HeaderName = "Bad Header" }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Clone_DropsTheGeneratedKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var config = new AxiosConfig { Url = server.Url + "x" };
        config.Idempotency.Enabled = true;

        await client.RequestAsync(config);
        var clone = config.Clone();

        Assert.Null(clone.Idempotency.GeneratedKey);
        Assert.True(clone.Idempotency.Enabled);
    }

    [Fact]
    public async Task MergedConfig_IsPerRequest_SoTheUserConfigKeepsNoKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var config = new AxiosConfig { Url = server.Url + "x" };
        config.Idempotency.Enabled = true;

        await client.RequestAsync(config);

        // 合并产物才有键：用户手里的配置不因为发过一次请求就粘上旧键。
        Assert.Null(config.Idempotency.GeneratedKey);
        Assert.False(string.IsNullOrWhiteSpace(server.Received[0].Headers[Header]));
    }
}
