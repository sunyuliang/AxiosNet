using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 可观测性设置的合并口径：按请求显式写 <c>null</c> 表示清除实例默认值，省略才继承。
/// </summary>
public sealed class ObservabilityMergeTests
{
    [Fact]
    public async Task InstanceCallback_PerRequestNull_IsCleared()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        var fired = 0;
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref fired) },
        });

        var response = await client.GetAsync("x", c => c.Observability.OnRawResponse = null);

        Assert.Equal(200, response.Status);
        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task InstanceCallback_OmittedInPerRequest_IsInheritedAndFires()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        var fired = 0;
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref fired) },
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task NoInstanceCallback_PerRequestNull_DoesNotThrow()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync("x", c => c.Observability.OnRawResponse = null);

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public void MergeFrom_ExplicitNullClearsEveryLeaf()
    {
        var target = new AxiosConfig.ObservabilitySettings
        {
            Capture = TelemetryCapture.Off,
            OnUploadProgress = static _ => { },
            OnDownloadProgress = static _ => { },
            OnRawResponse = static (_, _) => { },
            OnSendingRequest = static (_, _, _) => default,
            TimeoutMessage = "instance",
        };

        var incoming = new AxiosConfig.ObservabilitySettings
        {
            Capture = null,
            OnUploadProgress = null,
            OnDownloadProgress = null,
            OnRawResponse = null,
            OnSendingRequest = null,
            TimeoutMessage = null,
        };

        target.MergeFrom(incoming);

        Assert.Null(target.Capture);
        Assert.Null(target.OnUploadProgress);
        Assert.Null(target.OnDownloadProgress);
        Assert.Null(target.OnRawResponse);
        Assert.Null(target.OnSendingRequest);
        Assert.Null(target.TimeoutMessage);
    }

    [Fact]
    public void MergeFrom_ExplicitFalse_TurnsOffInstanceDefault()
    {
        var target = new AxiosConfig.ObservabilitySettings { CaptureRawResponseBody = true };

        target.MergeFrom(new AxiosConfig.ObservabilitySettings { CaptureRawResponseBody = false });

        Assert.False(target.CaptureRawResponseBody);
    }

    [Fact]
    public void MergeFrom_OmittedBool_KeepsInstanceDefault()
    {
        var target = new AxiosConfig.ObservabilitySettings { CaptureRawResponseBody = true };

        target.MergeFrom(new AxiosConfig.ObservabilitySettings());

        Assert.True(target.CaptureRawResponseBody);
    }

    [Fact]
    public void Clone_PreservesExplicitNullMarkers()
    {
        var source = new AxiosConfig.ObservabilitySettings
        {
            OnRawResponse = null,
            TimeoutMessage = null,
        };

        var clone = source.Clone();
        var target = new AxiosConfig.ObservabilitySettings
        {
            OnRawResponse = static (_, _) => { },
            TimeoutMessage = "instance",
        };

        target.MergeFrom(clone);

        Assert.Null(target.OnRawResponse);
        Assert.Null(target.TimeoutMessage);
    }

    // ------------------------------------------------------------------
    // 回放 response.Config：实例继承的回调 / 标记不能跨实例传播
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_GetWithResponseConfig_DoesNotCarrySourceInstanceCallback()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        var sourceFired = 0;
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref sourceFired) },
        });

        var first = await source.GetAsync("x");
        Assert.Equal(1, sourceFired);

        var targetFired = 0;
        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref targetFired) },
        });

        // 快捷方法回放：response.Config 里 OnRawResponse 的标记来自来源实例默认值，不是本次按请求写的。
        await target.GetAsync("y", first.Config!);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(1, targetFired);
        Assert.Equal(1, sourceFired);
    }

    [Fact]
    public async Task Replay_RequestAsync_DoesNotCarrySourceInstanceCallback()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        var sourceFired = 0;
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref sourceFired) },
        });

        var first = await source.GetAsync("x");
        Assert.Equal(1, sourceFired);

        var targetFired = 0;
        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref targetFired) },
        });

        // 通用入口回放：与快捷方法同一套「实例来源不搬运」口径。
        await target.RequestAsync(first.Config!);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(1, targetFired);
        Assert.Equal(1, sourceFired);
    }

    [Fact]
    public async Task Replay_ExplicitPerRequestNull_ClearsTargetInstanceCallback()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        var sourceFired = 0;
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref sourceFired) },
        });

        // 调用方在按请求配置里显式写 null：这是请求级意图，回放时必须保留。
        var first = await source.GetAsync("x", c => c.Observability.OnRawResponse = null);
        Assert.Equal(0, sourceFired);

        var targetFired = 0;
        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Observability = { OnRawResponse = (_, _) => Interlocked.Increment(ref targetFired) },
        });

        await target.RequestAsync(first.Config!);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(0, targetFired);
        Assert.Equal(0, sourceFired);
    }

    private static TestResponse Json() => new()
    {
        Body = "{}"u8.ToArray(),
        ContentType = "application/json",
    };
}
