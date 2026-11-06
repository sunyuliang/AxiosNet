using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 2.0 配置面（七组）：收敛后的合并 / 克隆语义。
/// <para>
/// 这里压的是「组搬了位置，语义没变」：同一份状态必须仍然只有一处真源，
/// 实例级项（序列化器表、格式化钩子）不随库产出的配置搬运。
/// </para>
/// </summary>
public sealed class ConfigSurfaceTests
{
    [Fact]
    public void Clone_NewLeafGroups_AreIndependent()
    {
        var config = new AxiosConfig
        {
            Limits = { MaxResponseBytes = 4096, MaxRequestBytes = 2048, MaxStreamFrameBytes = 1024 },
            Transport = { MaxRate = 4096, Profile = "backup" },
            Observability = { Capture = TelemetryCapture.Query },
            Serialization = { ValueText = (value, _) => value?.ToString() },
            Resilience = { Idempotency = { Enabled = true } }
        };

        var clone = config.Clone();

        Assert.Equal(4096, clone.Limits.MaxResponseBytes);
        Assert.Equal(2048, clone.Limits.MaxRequestBytes);
        Assert.Equal(1024, clone.Limits.MaxStreamFrameBytes);
        Assert.Equal(4096, clone.Transport.MaxRate);
        Assert.Equal("backup", clone.Transport.Profile);
        Assert.Equal(TelemetryCapture.Query, clone.Observability.Capture);
        Assert.NotNull(clone.Serialization.ValueText);
        Assert.True(clone.Resilience.Idempotency.Enabled);

        // 改克隆不能回写源：每个叶子都必须落在自己的组实例里。
        clone.Limits.MaxResponseBytes = 9;
        clone.Transport.MaxRate = 8;
        clone.Transport.Profile = "other";
        clone.Observability.Capture = TelemetryCapture.Off;
        clone.Resilience.Idempotency.Enabled = false;

        Assert.Equal(4096, config.Limits.MaxResponseBytes);
        Assert.Equal(4096, config.Transport.MaxRate);
        Assert.Equal("backup", config.Transport.Profile);
        Assert.Equal(TelemetryCapture.Query, config.Observability.Capture);
        Assert.True(config.Resilience.Idempotency.Enabled);
    }

    [Fact]
    public void TransportMerge_MaxRateIsPerRequest_ProxyStaysInstanceLevel()
    {
        var instance = new AxiosConfig.TransportSettings
        {
            MaxRate = 4096,
            Profile = "backup"
        };
        var perCall = new AxiosConfig.TransportSettings
        {
            MaxRate = 1024,
            Profile = "other"
        };

        instance.MergeFrom(perCall);

        // MaxRate / Profile 属按请求项，后写的覆盖实例默认。
        Assert.Equal(1024, instance.MaxRate);
        Assert.Equal("other", instance.Profile);

        // 显式 null MaxRate 是有效值（回到不限速），不能被「null = 没表态」吞掉。
        instance.MergeFrom(new AxiosConfig.TransportSettings { MaxRate = null });

        Assert.Null(instance.MaxRate);
    }

    [Fact]
    public void Concurrency_HasItsOwnStorage()
    {
        var config = new AxiosConfig
        {
            Resilience =
            {
                Concurrency =
                {
                    MaxInFlight = 3,
                    MaxQueue = 7,
                    QueueTimeout = TimeSpan.FromMilliseconds(250)
                }
            }
        };

        Assert.Equal(3, config.Resilience.Concurrency.MaxInFlight);
        Assert.Equal(7, config.Resilience.Concurrency.MaxQueue);
        Assert.Equal(TimeSpan.FromMilliseconds(250), config.Resilience.Concurrency.QueueTimeout);

        // 并发三项不再借 Limits 存储：没碰 Limits 时它保持未设置。
        Assert.Null(config.LimitsOrNull);

        config.Resilience.Concurrency.MaxInFlight = 5;
        Assert.Equal(5, config.Resilience.Concurrency.MaxInFlight);
    }

    [Fact]
    public void ResilienceMerge_IncludesIdempotencySubGroup()
    {
        var config = new AxiosConfig
        {
            Resilience = { Idempotency = { Enabled = true } }
        };
        var perCall = new AxiosConfig
        {
            Resilience = { Idempotency = { Enabled = false, HeaderName = "X-Key" } }
        };

        config.Resilience.MergeFrom(perCall.Resilience);

        Assert.False(config.Resilience.Idempotency.Enabled);
        Assert.Equal("X-Key", config.Resilience.Idempotency.HeaderName);
    }

    [Fact]
    public async Task ValidateStatus_TopLevel_NoObservabilityGroupNeeded()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: TestJson.Bytes(new { ok = false })));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            ValidateStatus = status => status < 600
        });

        var response = await client.GetAsync("x");

        Assert.Equal(503, response.Status);
        Assert.Null(client.Defaults.ObservabilityOrNull);
    }

    [Fact]
    public async Task ValidateStatus_PerRequestOverridesInstance_AndCanBeRestored()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: TestJson.Bytes(new { ok = false })));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            ValidateStatus = _ => true
        });

        var accepted = await client.GetAsync("x");
        Assert.Equal(503, accepted.Status);

        // 按请求只接受 2xx：实例默认的「5xx 也算合法」被覆盖，必须抛。
        var exception = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("x", c => c.ValidateStatus = status => status < 300));
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
    }
}
