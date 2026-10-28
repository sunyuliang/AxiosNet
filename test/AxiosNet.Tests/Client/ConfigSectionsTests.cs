using AxiosNet.Circuit;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 配置的七个子对象：克隆是否带全、合并是否只覆盖「本次显式设过」的项。
/// <para>
/// 两个层次一起放这里，因为目标是同一件事：<c>MergeFrom</c> 的空值语义（<c>null</c> ＝ 没表态 vs 显式关闭）。
/// 「合并了什么」由单元级断言直接压 <c>MergeFrom</c>（报文上看不出来），
/// 「合并层有没有真的接进管线」由真实请求的接线用例证明。
/// </para>
/// </summary>
public sealed class ConfigSectionsTests
{
    [Fact]
    public void Clone_CopiesEverySection()
    {
        var config = new AxiosConfig
        {
            BaseAddress = "https://example.com/",
            Timeout = TimeSpan.FromSeconds(7),
            Telemetry = new() { Enabled = false, CaptureUrl = false, CaptureQuery = true },
            Callbacks = new()
            {
                TimeoutMessage = "custom-timeout",
                ValidateStatus = status => status < 500
            },
            Connection = new() { MaxRedirects = 2 },
            Limits = new() { MaxContentLength = 4096, MaxBodyLength = 2048 },
            Retry = new() { Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(5)) },
            Circuit = new() { Policy = new CircuitBreaker(3, TimeSpan.FromSeconds(1)) },
            Hedging = new() { MaxHedges = 2, Delay = TimeSpan.FromMilliseconds(7), HedgeNonIdempotentMethods = true },
            Failover = new() { Endpoints = new[] { new Uri("https://backup.example.com/api") } }
        };

        var clone = config.Clone();

        Assert.Equal("https://example.com/", clone.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(7), clone.Timeout);
        Assert.False(clone.Telemetry.Enabled);
        Assert.False(clone.Telemetry.CaptureUrl);
        Assert.True(clone.Telemetry.CaptureQuery);
        Assert.Equal("custom-timeout", clone.Callbacks.TimeoutMessage);
        Assert.NotNull(clone.Callbacks.ValidateStatus);
        Assert.Equal(2, clone.Connection.MaxRedirects);
        Assert.Equal(4096, clone.Limits.MaxContentLength);
        Assert.Equal(2048, clone.Limits.MaxBodyLength);
        Assert.NotNull(clone.Retry.Policy);
        Assert.NotNull(clone.Circuit.Policy);
        Assert.Equal(2, clone.Hedging.MaxHedges);
        Assert.Equal(TimeSpan.FromMilliseconds(7), clone.Hedging.Delay);
        Assert.True(clone.Hedging.HedgeNonIdempotentMethods);
        Assert.Equal("https://backup.example.com/api", clone.Failover.Endpoints![0].AbsoluteUri);

        // 子对象必须是新实例：改克隆不能回写源配置
        clone.Telemetry.Enabled = true;
        Assert.False(config.Telemetry.Enabled);

        clone.Hedging.MaxHedges = 0;
        Assert.Equal(2, config.Hedging.MaxHedges);
    }

    [Fact]
    public async Task Merge_KeepsInstanceCallbacks_WhenPerRequestDoesNotTouchThem()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: TestJson.Bytes(new { ok = false })));

        // 实例上配「5xx 也算合法」，本次请求只改请求头 —— 合并不能把实例回调擦掉
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Callbacks = new() { ValidateStatus = status => status < 600 }
        });

        var response = await client.GetAsync("x", c => c.Headers.Set("X-Trace", "1"));

        Assert.Equal(503, response.Status);
        Assert.Equal("1", server.Received[0].Headers["x-trace"]);
    }

    [Fact]
    public async Task Merge_KeepsInstanceRetryCallback_WhenPerRequestOnlySetsPolicy()
    {
        var attempts = 0;
        var reported = 0;

        await using var server = TestHttpServer.Start(req =>
            req.RequestNumberOnConnection == 1
                ? new TestResponse(503, Body: Array.Empty<byte>())
                : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = new() { OnRetry = _ => Interlocked.Increment(ref reported) }
        });

        var response = await client.GetAsync("x", c =>
        {
            Interlocked.Increment(ref attempts);
            c.Retry.Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(5));
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(1, attempts);
        Assert.Equal(1, reported);
        Assert.Equal(2, server.Received.Count);
    }

    // ------------------------------------------------------------------
    // RetrySettings
    // ------------------------------------------------------------------

    [Fact]
    public void RetryMerge_UntouchedIncoming_KeepsInstancePolicyAndCallback()
    {
        var policy = RetryPolicy.Constant(3, TimeSpan.FromMilliseconds(1));
        Action<AxiosRetryContext> onRetry = _ => { };
        var settings = new AxiosConfig.RetrySettings { Policy = policy, OnRetry = onRetry };

        settings.MergeFrom(new AxiosConfig.RetrySettings());

        Assert.Same(policy, settings.Policy);
        Assert.Same(onRetry, settings.OnRetry);
    }

    [Fact]
    public void RetryMerge_ExplicitNullPolicy_TurnsRetryOff()
    {
        var settings = new AxiosConfig.RetrySettings
        {
            Policy = RetryPolicy.Constant(3, TimeSpan.FromMilliseconds(1))
        };

        // 显式赋 null 是「本次请求关掉实例默认的重试」，不是「没表态」
        settings.MergeFrom(new AxiosConfig.RetrySettings { Policy = null });

        Assert.Null(settings.Policy);
        Assert.True(settings.PolicySet);
    }

    [Fact]
    public void RetryMerge_IncomingPolicyAndCallback_ReplaceBoth()
    {
        var instancePolicy = RetryPolicy.Constant(3, TimeSpan.FromMilliseconds(1));
        var incomingPolicy = RetryPolicy.Constant(1, TimeSpan.FromMilliseconds(2));
        Action<AxiosRetryContext> instanceCallback = _ => { };
        Action<AxiosRetryContext> incomingCallback = _ => { };

        var settings = new AxiosConfig.RetrySettings { Policy = instancePolicy, OnRetry = instanceCallback };

        settings.MergeFrom(new AxiosConfig.RetrySettings { Policy = incomingPolicy, OnRetry = incomingCallback });

        Assert.Same(incomingPolicy, settings.Policy);
        Assert.Same(incomingCallback, settings.OnRetry);
    }

    // ------------------------------------------------------------------
    // CircuitSettings
    // ------------------------------------------------------------------

    [Fact]
    public void CircuitMerge_UntouchedIncoming_KeepsInstancePolicyAndCallback()
    {
        var policy = new CircuitBreaker(3, TimeSpan.FromSeconds(1));
        Action<AxiosCircuitContext> onRejected = _ => { };
        var settings = new AxiosConfig.CircuitSettings { Policy = policy, OnRejected = onRejected };

        settings.MergeFrom(new AxiosConfig.CircuitSettings());

        Assert.Same(policy, settings.Policy);
        Assert.Same(onRejected, settings.OnRejected);
        Assert.False(new AxiosConfig.CircuitSettings().PolicySet);
    }

    [Fact]
    public void CircuitMerge_ExplicitNullPolicy_TurnsCircuitOff()
    {
        var settings = new AxiosConfig.CircuitSettings
        {
            Policy = new CircuitBreaker(3, TimeSpan.FromSeconds(1))
        };

        settings.MergeFrom(new AxiosConfig.CircuitSettings { Policy = null });

        Assert.Null(settings.Policy);
        Assert.True(settings.PolicySet);
    }

    [Fact]
    public void CircuitMerge_IncomingPolicyAndCallback_ReplaceBoth()
    {
        var instancePolicy = new CircuitBreaker(3, TimeSpan.FromSeconds(1));
        var incomingPolicy = new CircuitBreaker(5, TimeSpan.FromSeconds(2));
        Action<AxiosCircuitContext> instanceCallback = _ => { };
        Action<AxiosCircuitContext> incomingCallback = _ => { };

        var settings = new AxiosConfig.CircuitSettings { Policy = instancePolicy, OnRejected = instanceCallback };

        settings.MergeFrom(new AxiosConfig.CircuitSettings { Policy = incomingPolicy, OnRejected = incomingCallback });

        Assert.Same(incomingPolicy, settings.Policy);
        Assert.Same(incomingCallback, settings.OnRejected);
    }

    // ------------------------------------------------------------------
    // HedgingSettings
    // ------------------------------------------------------------------

    [Fact]
    public void HedgingMerge_UntouchedIncoming_KeepsInstanceValues()
    {
        var settings = new AxiosConfig.HedgingSettings
        {
            MaxHedges = 2,
            Delay = TimeSpan.FromMilliseconds(7),
            HedgeNonIdempotentMethods = true
        };

        settings.MergeFrom(new AxiosConfig.HedgingSettings());

        Assert.Equal(2, settings.MaxHedges);
        Assert.Equal(TimeSpan.FromMilliseconds(7), settings.Delay);
        Assert.True(settings.HedgeNonIdempotentMethods);
    }

    [Fact]
    public void HedgingMerge_ExplicitValues_OverrideOneByOne()
    {
        var settings = new AxiosConfig.HedgingSettings
        {
            MaxHedges = 2,
            Delay = TimeSpan.FromMilliseconds(7),
            HedgeNonIdempotentMethods = true
        };

        // 显式 0 是「本次请求关掉对冲」，不能按「没表态」处理
        settings.MergeFrom(new AxiosConfig.HedgingSettings { MaxHedges = 0 });

        Assert.Equal(0, settings.MaxHedges);
        Assert.Equal(TimeSpan.FromMilliseconds(7), settings.Delay);
        Assert.True(settings.HedgeNonIdempotentMethods);

        settings.MergeFrom(new AxiosConfig.HedgingSettings
        {
            Delay = TimeSpan.Zero,
            HedgeNonIdempotentMethods = false
        });

        Assert.Equal(TimeSpan.Zero, settings.Delay);
        Assert.False(settings.HedgeNonIdempotentMethods);
    }

    // ------------------------------------------------------------------
    // FailoverSettings
    // ------------------------------------------------------------------

    [Fact]
    public void FailoverMerge_UntouchedIncoming_KeepsInstanceEndpoints()
    {
        var endpoints = new[] { new Uri("https://backup.example.com/") };
        var settings = new AxiosConfig.FailoverSettings { Endpoints = endpoints };

        settings.MergeFrom(new AxiosConfig.FailoverSettings());

        // 赋值时快照成数组，所以比内容而不是引用：没被 incoming 覆盖才是这条用例要的。
        Assert.Equal(endpoints, settings.Endpoints);
    }

    [Fact]
    public void FailoverMerge_ExplicitNull_ClearsEndpoints()
    {
        var settings = new AxiosConfig.FailoverSettings
        {
            Endpoints = new[] { new Uri("https://backup.example.com/") }
        };

        settings.MergeFrom(new AxiosConfig.FailoverSettings { Endpoints = null });

        Assert.Null(settings.Endpoints);
        Assert.True(settings.EndpointsSet);
    }

    // ------------------------------------------------------------------
    // CallbackSettings
    // ------------------------------------------------------------------

    [Fact]
    public void CallbacksMerge_UntouchedIncoming_KeepsEveryInstanceCallback()
    {
        Action<AxiosProgressEvent> upload = _ => { };
        Action<AxiosProgressEvent> download = _ => { };
        Func<int, bool> validate = status => status < 600;

        var settings = new AxiosConfig.CallbackSettings
        {
            OnUploadProgress = upload,
            OnDownloadProgress = download,
            TimeoutMessage = "instance-timeout",
            ValidateStatus = validate
        };

        settings.MergeFrom(new AxiosConfig.CallbackSettings());

        Assert.Same(upload, settings.OnUploadProgress);
        Assert.Same(download, settings.OnDownloadProgress);
        Assert.Equal("instance-timeout", settings.TimeoutMessage);
        Assert.Same(validate, settings.ValidateStatus);
    }

    [Fact]
    public void CallbacksMerge_IncomingValues_ReplaceOneByOne()
    {
        Action<AxiosProgressEvent> upload = _ => { };
        Action<AxiosProgressEvent> download = _ => { };
        Func<int, bool> validate = status => status < 500;

        var settings = new AxiosConfig.CallbackSettings
        {
            OnUploadProgress = _ => { },
            OnDownloadProgress = _ => { },
            TimeoutMessage = "instance-timeout",
            ValidateStatus = status => status < 600
        };

        // 只覆盖上传回调与超时文案，下载回调与状态校验必须原样保留
        settings.MergeFrom(new AxiosConfig.CallbackSettings
        {
            OnUploadProgress = upload,
            TimeoutMessage = "per-call-timeout"
        });

        Assert.Same(upload, settings.OnUploadProgress);
        Assert.Equal("per-call-timeout", settings.TimeoutMessage);
        Assert.NotNull(settings.OnDownloadProgress);
        Assert.True(settings.ValidateStatus!(503));

        settings.MergeFrom(new AxiosConfig.CallbackSettings
        {
            OnDownloadProgress = download,
            ValidateStatus = validate
        });

        Assert.Same(download, settings.OnDownloadProgress);
        Assert.Same(validate, settings.ValidateStatus);
    }

    // ------------------------------------------------------------------
    // TelemetrySettings
    // ------------------------------------------------------------------

    [Fact]
    public void TelemetryMerge_UntouchedIncoming_KeepsEveryInstanceFlag()
    {
        var settings = new AxiosConfig.TelemetrySettings
        {
            Enabled = false,
            CaptureUrl = false,
            CaptureQuery = true
        };

        settings.MergeFrom(new AxiosConfig.TelemetrySettings());

        Assert.False(settings.Enabled);
        Assert.False(settings.CaptureUrl);
        Assert.True(settings.CaptureQuery);
    }

    [Fact]
    public void TelemetryMerge_ExplicitFlags_OverrideOneByOne()
    {
        var settings = new AxiosConfig.TelemetrySettings
        {
            Enabled = false,
            CaptureUrl = false,
            CaptureQuery = false
        };

        // 只表态 CaptureQuery：另外两项保持实例值（false 是有效值，不能被「null 才算设置」吞掉）
        settings.MergeFrom(new AxiosConfig.TelemetrySettings { CaptureQuery = true });

        Assert.False(settings.Enabled);
        Assert.False(settings.CaptureUrl);
        Assert.True(settings.CaptureQuery);

        settings.MergeFrom(new AxiosConfig.TelemetrySettings { Enabled = true, CaptureUrl = true });

        Assert.True(settings.Enabled);
        Assert.True(settings.CaptureUrl);
        Assert.True(settings.CaptureQuery);
    }

    // ------------------------------------------------------------------
    // 合并层真实接线：单次请求显式 null 必须真的关掉实例策略
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestNullRetryPolicy_DisablesInstanceRetry()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = new() { Policy = RetryPolicy.Constant(3, TimeSpan.FromMilliseconds(5)) }
        });

        await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("x", c => c.Retry.Policy = null));

        // 实例默认策略被本次请求显式关掉：只发一次，不做退避重试
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestNullCircuitPolicy_DisablesInstanceCircuit()
    {
        var breaker = new CircuitBreaker(failureThreshold: 1, breakDuration: TimeSpan.FromMinutes(5));

        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Circuit = new() { Policy = breaker }
        });

        // 先让熔断器进入 Open（实例默认策略生效）
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));
        Assert.Single(server.Received);

        // 本次请求显式关掉熔断：闸门直接放行，请求真的发出去
        await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("x", c => c.Circuit.Policy = null));
        Assert.Equal(2, server.Received.Count);
    }
}
