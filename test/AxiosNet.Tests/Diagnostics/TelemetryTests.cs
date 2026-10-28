
using AxiosNet.Pooling;
using AxiosNet.Retry;
using AxiosNet.Telemetry;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;

namespace AxiosNet.Tests.Diagnostics;

/// <summary>
/// 可观测性：Activity span 语义标签、Meter 指标、DiagnosticListener 事件、采集脱敏。
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryTests
{
    // ------------------------------------------------------------------
    // 链路：span 与语义约定标签
    // ------------------------------------------------------------------

    [Fact]
    public async Task Span_CarriesSemanticConventionTags()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("span");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + path);

        var span = Assert.Single(probe.Spans);

        // span 名按语义约定取「方法」（与 .NET 8 的 HttpClient 一致），URL 放标签里
        Assert.Equal("GET", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);

        Assert.Equal("GET", span.GetTagItem(TagMethod));
        Assert.Equal("http", span.GetTagItem(TagScheme));
        Assert.Equal("127.0.0.1", span.GetTagItem(TagServerAddress));
        Assert.Equal(server.Port, span.GetTagItem(TagServerPort));
        Assert.Equal("/" + path, span.GetTagItem(TagPath));
        Assert.Equal(server.Url + path, span.GetTagItem(TagFullUrl));

        // 响应侧：状态码、协商到的协议、响应字节数
        Assert.Equal(200, span.GetTagItem(TagStatusCode));
        Assert.Equal(response.ProtocolVersion!.Major == 2 ? "2" : "1.1", span.GetTagItem(TagProtocolVersion));
        Assert.Equal(TestJson.Bytes(new { ok = true }).Length, Assert.IsType<long>(span.GetTagItem(TagResponseBodySize)));
        Assert.Null(span.GetTagItem(TagErrorType));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Span_RecordsRequestBodySize()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));
        var path = UniquePath("body-size");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await client.PostAsync(server.Url + path, new { name = "bob" });

        var span = Assert.Single(probe.Spans);
        Assert.Equal(TestJson.Bytes(new { name = "bob" }).Length, Assert.IsType<long>(span.GetTagItem(TagRequestBodySize)));
    }

    [Fact]
    public async Task Span_OnHttpError_RecordsStatusAndErrorType()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        var path = UniquePath("http-error");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + path));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        var span = Assert.Single(probe.Spans);
        Assert.Equal(500, span.GetTagItem(TagStatusCode));
        // 语义约定：有状态码时 error.type 就取状态码
        Assert.Equal("500", span.GetTagItem(TagErrorType));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Span_OnAcceptedNon2xx_IsStillMarkedError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 404));
        var path = UniquePath("accepted-404");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig
        {
            // 调用方自己接受 404：请求不抛，但 HTTP 层面它仍是错误响应
            Callbacks = { ValidateStatus = status => status is >= 200 and < 300 or 404 }
        });

        var response = await client.GetAsync(server.Url + path);
        Assert.Equal(404, response.Status);

        var span = Assert.Single(probe.Spans);
        Assert.Equal("404", span.GetTagItem(TagErrorType));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Span_OnHttpError_RecordsLibraryErrorCode()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        var path = UniquePath("error-code");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + path));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        var span = Assert.Single(probe.Spans);

        // error.type 与库错误码是两个维度：前者是「服务端说了什么」（语义约定要求状态码优先），
        // 后者是「库把这次失败归到哪一类」。只留 error.type 的话，「503 被归成 ERR_BAD_RESPONSE」
        // 这个信息在 span 上就没了（只能翻异常消息）。
        Assert.Equal("503", span.GetTagItem(TagErrorType));
        Assert.Equal(AxiosException.ErrBadResponse, span.GetTagItem(TagErrorCode));
    }

    [Fact]
    public async Task Span_OnNetworkError_RecordsErrorCodeAndExceptionEvent()
    {
        var port = ClosedPort();
        var path = UniquePath("network-error");
        using var probe = new TelemetryProbe(port, path);

        using var client = new AxiosClient();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync($"http://127.0.0.1:{port}/{path}"));
        Assert.Equal(AxiosException.ErrNetwork, ex.Code);

        var span = Assert.Single(probe.Spans);
        Assert.Equal(AxiosException.ErrNetwork, span.GetTagItem(TagErrorType));
        // 没有状态码时两者取值相同：仍然写一次，后端不必按「有没有 status」分支
        Assert.Equal(AxiosException.ErrNetwork, span.GetTagItem(TagErrorCode));
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        // net6.0 没有 Activity.AddException，库按语义约定手记一条 exception 事件
        var exceptionEvent = Assert.Single(span.Events, e => e.Name == "exception");
        Assert.Equal(typeof(AxiosException).FullName, exceptionEvent.Tags.First(t => t.Key == "exception.type").Value);
    }

    [Fact]
    public async Task CallerActivity_BecomesParentSpan()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("parent");
        using var probe = new TelemetryProbe(server.Port, path);

        using var callerSource = new ActivitySource("AxiosNet.Tests.Caller");
        using var callerListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == callerSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(callerListener);

        using var caller = callerSource.StartActivity("caller", ActivityKind.Internal);
        Assert.NotNull(caller);

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path);

        var span = Assert.Single(probe.Spans);
        Assert.Equal(caller!.SpanId, span.ParentSpanId);
        Assert.Equal(caller.TraceId, span.TraceId);
    }

    [Fact]
    public async Task TraceContext_PropagatesToServer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("propagation");
        using var probe = new TelemetryProbe(server.Port, path);

        using var callerSource = new ActivitySource("AxiosNet.Tests.Propagation");
        using var callerListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == callerSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(callerListener);

        using var caller = callerSource.StartActivity("caller", ActivityKind.Internal);
        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path);

        // 本库的 span 是父，BCL 的 DiagnosticsHandler 沿 Activity.Current 注入 traceparent
        Assert.Single(probe.Spans);
        var received = Assert.Single(server.Received);
        Assert.True(received.Headers.TryGetValue("traceparent", out var traceParent), "服务端没有收到 traceparent");
        Assert.Contains(caller!.TraceId.ToHexString(), traceParent, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // 连接池：租约 / 节点 / 驱逐
    // ------------------------------------------------------------------

    [Fact]
    public async Task Span_RecordsWhetherPoolEndpointWasReused()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("pool-reuse");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path);
        await client.GetAsync(server.Url + path);

        var spans = probe.Spans;
        Assert.Equal(2, spans.Count);

        // 第一个请求顺手新建了 origin 节点（新 Handler + 新 HttpClient），第二个复用它 ——
        // 「首个请求为什么慢」在 trace 里能直接看出来，不用去猜是不是 TLS 握手
        Assert.Equal("false", spans[0].GetTagItem(TagEndpointReused));
        Assert.Equal("true", spans[1].GetTagItem(TagEndpointReused));
    }

    [Fact]
    public async Task Metrics_PoolLeases_DistinguishNewEndpointFromReuse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("pool-leases");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path);
        await client.GetAsync(server.Url + path);

        var leases = probe.MeasurementsOf(AxiosTelemetry.PoolLeasesInstrumentName);
        Assert.Equal(2, leases.Count);
        Assert.All(leases, measurement => Assert.Equal(1d, measurement.Value));

        // 复用率由后端用这两个序列算，库这里只给事实（每租一次 1，带 reused 标签）
        Assert.Equal("false", leases[0].Tag(TagEndpointReused));
        Assert.Equal("true", leases[1].Tag(TagEndpointReused));
        Assert.Equal("127.0.0.1", leases[0].Tag(TagServerAddress));
        Assert.Equal(server.Port, leases[0].Tag(TagServerPort));

        // 两次租约只建了一个节点：与 leases 一除就是节点命中率
        var created = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsCreatedInstrumentName));
        Assert.Equal(1d, created.Value);
        Assert.Equal(server.Port, created.Tag(TagServerPort));
    }

    [Fact]
    public async Task Metrics_PoolEviction_RecordsReasonForManualEviction()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("pool-evict-manual");
        using var probe = new TelemetryProbe(server.Port, path);

        // EvictionInterval = 0：不起后台定时器，否则它可能在本用例断言前就把节点扫掉
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url + path);
        Assert.Equal(1, pool.EvictIdleEndpoints());

        var evicted = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsEvictedInstrumentName));
        Assert.Equal(1d, evicted.Value);
        Assert.Equal("manual", evicted.Tag(TagEvictionReason));
        Assert.Equal(server.Port, evicted.Tag(TagServerPort));
    }

    [Fact]
    public async Task Metrics_PoolEviction_RecordsReasonForCapacityPressure()
    {
        await using var first = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("pool-evict-capacity");
        using var probe = new TelemetryProbe(first.Port, path);

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,          // 第二个 origin 只能靠驱逐第一个腾位置
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(first.Url + path);
        await client.GetAsync(second.Url + path);

        var evicted = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsEvictedInstrumentName));
        Assert.Equal("capacity", evicted.Tag(TagEvictionReason));
        Assert.Equal(first.Port, evicted.Tag(TagServerPort));
    }

    [Fact]
    public async Task Metrics_PoolEviction_RecordsReasonForDisposeWhileLeaseInFlight()
    {
        // 关闭时还有请求在飞：Dispose 只处理「已经空闲」的节点，在途那个先打 DisposeWhenIdle 标记、
        // 等最后一条租约归还才真正释放（Forget）。这条路径以前不上报，会让
        // 「created - evicted = 当前节点数」在「关闭时还有请求在飞」的场景下对不上。
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { ok = true }) with { BodyDelay = TimeSpan.FromMilliseconds(500) });

        var path = UniquePath("pool-evict-inflight");
        using var probe = new TelemetryProbe(server.Port, path);

        var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });
        using var client = new AxiosClient(pool: pool);

        // 流式：拿到响应头就返回，租约还捏在正文流手里
        var response = await client.GetAsync(server.Url + path, configure: c => c.ResponseType = ResponseType.Stream);

        pool.Dispose();
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsEvictedInstrumentName));

        await Assert.IsAssignableFrom<Stream>(response.Data).DisposeAsync();

        var evicted = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsEvictedInstrumentName));
        Assert.Equal("dispose", evicted.Tag(TagEvictionReason));
        Assert.Equal(server.Port, evicted.Tag(TagServerPort));

        // 重复 Dispose 不能重复上报（TryRemove 失败即不上报）
        pool.Dispose();
        Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsEvictedInstrumentName));
    }

    [Fact]
    public async Task Metrics_ActiveLeases_StayUpWhileStreamingBodyIsOpen()
    {
        // 活跃租约是进程级水位（无标签，无法按用例隔离），与 active_requests 同一套断言策略：
        // 只钉「下载中 >= 1、归还后 == 0」。本集合禁并行，此刻进程里没有别的请求。
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == AxiosTelemetry.ActiveLeasesInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (observed)
            {
                observed.Add(value);
            }
        });
        listener.Start();

        // 响应头先发、正文延迟 500ms：这一段正是 active_requests 已经归零、租约却还占着的窗口
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { ok = true }) with { BodyDelay = TimeSpan.FromMilliseconds(500) });

        using var client = new AxiosClient();
        var response = await client.GetAsync(
            server.Url + UniquePath("active-leases"),
            configure: c => c.ResponseType = ResponseType.Stream);

        listener.RecordObservableInstruments();
        lock (observed)
        {
            Assert.Contains(observed, value => value >= 1);
        }

        var body = Assert.IsAssignableFrom<Stream>(response.Data);
        await body.DisposeAsync();

        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(5))
        {
            listener.RecordObservableInstruments();
            lock (observed)
            {
                if (observed[observed.Count - 1] == 0)
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        lock (observed)
        {
            Assert.NotEmpty(observed);
            Assert.Equal(0, observed[observed.Count - 1]);
        }
    }

    [Fact]
    public async Task PoolReuseTag_IsWrittenToLibrarySpanOnly()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("pool-reuse-owner");
        using var probe = new TelemetryProbe(server.Port, path);

        using var callerSource = new ActivitySource("AxiosNet.Tests.PoolReuseCaller");
        using var callerListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == callerSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(callerListener);

        using var client = new AxiosClient();

        // ① 遥测开：标签写在库自己的 span 上，调用方的 span 一个字节都不许动
        using (var caller = callerSource.StartActivity("caller-on", ActivityKind.Internal))
        {
            Assert.NotNull(caller);
            await client.GetAsync(server.Url + path);
            Assert.Equal("false", Assert.Single(probe.Spans).GetTagItem(TagEndpointReused));
            Assert.Empty(caller.TagObjects);
        }

        // ② 遥测关：没有库的 span，此时 Activity.Current 就是调用方的 span ——
        // 这才是「归属判定」真正起作用的场景（少了它，库的标签会直接写进别人的 span）。
        using (var caller = callerSource.StartActivity("caller-off", ActivityKind.Internal))
        {
            Assert.NotNull(caller);
            await client.GetAsync(server.Url + path, configure: c => c.Telemetry.Enabled = false);
            Assert.Single(probe.Spans);      // 只有第 ① 次那条
            Assert.Empty(caller.TagObjects);
        }
    }

    [Fact]
    public async Task PoolReuseTag_KeepsFirstAttemptValueAcrossRetries()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) <= 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        var path = UniquePath("pool-reuse-retry");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) } });
        await client.GetAsync(server.Url + path);

        // 第 1 次尝试建了节点（false），第 2 次是复用（true）：标签必须留住「这个请求付了建节点的代价」。
        // 直接覆盖的话，重试过的请求会一律显示 true —— 恰好是「慢请求」那一类，信息正好反了。
        var span = Assert.Single(probe.Spans);
        Assert.Equal("false", span.GetTagItem(TagEndpointReused));

        // 两次尝试 = 两次租约：leases 数的是「租约次数」，不是请求数（请求维度看 request.duration）
        var leases = probe.MeasurementsOf(AxiosTelemetry.PoolLeasesInstrumentName);
        Assert.Equal(2, leases.Count);
        Assert.All(leases, measurement => Assert.Equal(1d, measurement.Value));
    }

    // ------------------------------------------------------------------
    // 重试
    // ------------------------------------------------------------------

    [Fact]
    public async Task Retry_SetsResendCount_EmitsEvent_AndCountsMetric()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) <= 2
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        var path = UniquePath("retry");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = new RetryPolicy(3, TimeSpan.Zero) } });
        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);

        // 一次逻辑请求 = 一个 span：重试次数记在 span 上，而不是再开一个 span
        var span = Assert.Single(probe.Spans);
        Assert.Equal(2, span.GetTagItem(TagResendCount));
        Assert.Equal(2, span.Events.Count(e => e.Name == "retry"));

        // 指标：每次「决定重试」计 1（两条测量，各 1 次）
        var retries = probe.MeasurementsOf(AxiosTelemetry.RetriesInstrumentName);
        Assert.Equal(2, retries.Count);
        Assert.All(retries, measurement => Assert.Equal(1d, measurement.Value));
        Assert.Equal("GET", retries[0].Tag(TagMethod));
        Assert.Equal(server.Port, retries[0].Tag(TagServerPort));
        Assert.Equal("500", retries[0].Tag(TagErrorType));

        // 事件：两条 Retry，载荷就是 OnRetry 拿到的那个上下文
        var retryEvents = probe.Events.Where(e => e.Name == AxiosTelemetry.RetryEventName).ToArray();
        Assert.Equal(2, retryEvents.Length);
        var context = Assert.IsType<AxiosRetryContext>(retryEvents[0].Payload);
        Assert.Equal(1, context.Attempt);
        Assert.Equal(500, context.StatusCode);
        Assert.Equal(AxiosException.ErrBadResponse, context.ErrorCode);
    }

    // ------------------------------------------------------------------
    // 指标
    // ------------------------------------------------------------------

    [Fact]
    public async Task Metrics_RecordsDurationWithLowCardinalityTags()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("duration");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path);

        var duration = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.True(duration.Value > 0, "耗时应当大于 0 秒");
        Assert.True(duration.Value < 5, $"耗时 {duration.Value}s 不合理");
        Assert.Equal("GET", duration.Tag(TagMethod));
        Assert.Equal("127.0.0.1", duration.Tag(TagServerAddress));
        Assert.Equal(server.Port, duration.Tag(TagServerPort));
        Assert.Equal(200, duration.Tag(TagStatusCode));
        Assert.Null(duration.Tag(TagErrorType));
    }

    [Fact]
    public async Task Metrics_RecordsErrorTypeOnFailure()
    {
        var port = ClosedPort();
        var path = UniquePath("duration-error");
        using var probe = new TelemetryProbe(port, path);

        using var client = new AxiosClient();
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync($"http://127.0.0.1:{port}/{path}"));

        var duration = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Equal(AxiosException.ErrNetwork, duration.Tag(TagErrorType));
        Assert.Null(duration.Tag(TagStatusCode));
    }

    /// <summary>
    /// instrument 的元数据（名字 / 类型 / 单位 / 描述 / Meter 名与版本）本身就是对外契约：
    /// OpenTelemetry 的 <c>AddMeter("AxiosNet")</c> 收到的就是这些，改名或改单位会让已经画好的仪表盘静默失效。
    /// <para>
    /// 这条用例不需要引 OpenTelemetry 包 —— SDK 最终也是通过 <see cref="MeterListener"/> 拿 instrument，
    /// 所以「MeterListener 看到什么」就是「SDK 看到什么」。
    /// </para>
    /// </summary>
    [Fact]
    public void Metrics_InstrumentMetadata_IsStableForOpenTelemetry()
    {
        var instruments = new List<Instrument>();
        using var listener = new MeterListener
        {
            // 只记录元数据，不 EnableMeasurementEvents：不给别的用例加测量开销
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == AxiosTelemetry.MeterName)
                {
                    instruments.Add(instrument);
                }
            }
        };
        listener.Start();

        Assert.Equal(9, instruments.Count);   // 指标数量也是契约：新增一个就要同步双版 README

        // Meter 版本跟程序集走：后端据此区分「同一个源的不同版本」
        var assemblyVersion = typeof(AxiosTelemetry).Assembly.GetName().Version?.ToString();
        Assert.All(instruments, instrument => Assert.Equal(assemblyVersion, instrument.Meter.Version));

        var duration = Assert.IsType<Histogram<double>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Equal("s", duration.Unit);     // 秒：与 OpenTelemetry HTTP semconv 的时长单位一致
        Assert.False(string.IsNullOrWhiteSpace(duration.Description));

        var active = Assert.IsType<ObservableGauge<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.ActiveRequestsInstrumentName));
        Assert.Equal("{request}", active.Unit);

        var retries = Assert.IsType<Counter<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.RetriesInstrumentName));
        Assert.Equal("{retry}", retries.Unit);

        // 池域：语义约定里没有对应项（内置指标只覆盖 socket 层），单位用 {lease} / {endpoint}
        var leases = Assert.IsType<Counter<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.PoolLeasesInstrumentName));
        Assert.Equal("{lease}", leases.Unit);

        var created = Assert.IsType<Counter<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.PoolEndpointsCreatedInstrumentName));
        Assert.Equal("{endpoint}", created.Unit);

        var evicted = Assert.IsType<Counter<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.PoolEndpointsEvictedInstrumentName));
        Assert.Equal("{endpoint}", evicted.Unit);

        var activeLeases = Assert.IsType<ObservableGauge<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.ActiveLeasesInstrumentName));
        Assert.Equal("{lease}", activeLeases.Unit);

        // 池的个数（不是池里的连接数）：这条水位上涨 = 有代码在反复建池
        var activePools = Assert.IsType<ObservableGauge<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.ActivePoolsInstrumentName));
        Assert.Equal("{pool}", activePools.Unit);

        var sharedPools = Assert.IsType<ObservableGauge<long>>(
            Assert.Single(instruments, instrument => instrument.Name == AxiosTelemetry.SharedPoolsInstrumentName));
        Assert.Equal("{pool}", sharedPools.Unit);
    }

    // 存活池数是进程级水位（无标签）：只钉方向——自己这批池被算进去、释放后掉下来；
    // 不钉绝对值（并行集合此刻也可能在建池）。这条指标存在的意义就是让「反复建池」能在面板上看见。
    [Fact]
    public void Metrics_ActivePools_TracksLivePools()
    {
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == AxiosTelemetry.ActivePoolsInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (observed)
            {
                observed.Add(value);
            }
        });
        listener.Start();

        long Sample()
        {
            lock (observed)
            {
                observed.Clear();
            }

            listener.RecordObservableInstruments();

            lock (observed)
            {
                return observed.Count == 0 ? 0 : observed[^1];
            }
        }

        var before = Sample();

        // 不建后台定时器：这条用例只验计数，64 条池足够压过并行集合的噪声
        var pools = Enumerable.Range(0, 64)
            .Select(_ => new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero }))
            .ToArray();

        var during = Sample();
        Assert.True(during >= before + pools.Length, $"存活池数没计入新建的池：{before} -> {during}");

        foreach (var pool in pools)
        {
            pool.Dispose();
        }

        var after = Sample();

        // 留一半余量给并行集合：只要求「自己这批的份量确实掉下去了」
        Assert.True(after <= during - (pools.Length / 2), $"释放后存活池数没掉下来：{during} -> {after}");
    }

    [Fact]
    public async Task Metrics_ActiveRequests_RisesWhileRequestInFlight()
    {
        // 在途数是全进程水位（ObservableGauge），没有 per-request 标签，无法按用例隔离：
        // 这里只钉「请求在飞时它 >= 1」这个不会因并行用例而误判的断言。
        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == AxiosTelemetry.ActiveRequestsInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (observed)
            {
                observed.Add(value);
            }
        });
        listener.Start();

        // 正文延迟 300ms：让请求真的「在途」一会儿，否则收集时它可能已经结束了
        await using var server = TestHttpServer.Start(
            _ => TestJson.Response(new { ok = true }) with { BodyDelay = TimeSpan.FromMilliseconds(300) });

        using var client = new AxiosClient();
        var pending = client.GetAsync(server.Url + UniquePath("active"));

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            listener.RecordObservableInstruments();
            lock (observed)
            {
                if (observed.Exists(value => value >= 1))
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        await pending;

        // 请求结束后必须回落到 0：只断言「在飞时 >= 1」的话，ExitRequest 漏调（在途数只增不减）
        // 这种 bug 永远不会被发现 —— 它会一直涨到进程重启。
        // 本集合禁并行（见 TelemetryCollection），所以此刻进程里没有别的请求在飞。
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(5))
        {
            listener.RecordObservableInstruments();
            lock (observed)
            {
                if (observed[observed.Count - 1] == 0)
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        lock (observed)
        {
            Assert.NotEmpty(observed);
            Assert.Contains(observed, value => value >= 1);
            Assert.Equal(0, observed[observed.Count - 1]);
        }
    }

    [Fact]
    public async Task DurationAndSpan_MeasureTheSameWindow_ExcludingRequestInterceptors()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("window");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();

        // 准备阶段故意慢 200ms：它属于调用方自己的加工，不该算进「接口耗时」
        client.Interceptors.Request.Use(async config =>
        {
            await Task.Delay(200);
            return config;
        });

        await client.GetAsync(server.Url + path);

        var span = Assert.Single(probe.Spans);
        var duration = Assert.Single(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));

        // ① 两者都不含准备阶段（否则会出现 200ms 量级的数字）
        Assert.True(
            span.Duration < TimeSpan.FromMilliseconds(150),
            $"span 时长 {span.Duration.TotalMilliseconds:0.0}ms 把请求拦截器的 200ms 算进去了。");
        Assert.True(
            duration.Value < 0.15,
            $"request.duration {duration.Value:0.000}s 把请求拦截器的 200ms 算进去了。");

        // ② 两者量的是同一段：trace 与指标对同一次请求给出不一致的耗时，是最难排查的那类「数据不对」
        Assert.True(
            Math.Abs(duration.Value - span.Duration.TotalSeconds) < 0.05,
            $"指标 {duration.Value:0.000}s 与 span {span.Duration.TotalSeconds:0.000}s 口径不一致。");
    }

    // ------------------------------------------------------------------
    // 诊断事件
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_EmitStartAndStop()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("events-ok");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + path);

        var start = Assert.IsType<AxiosRequestStartEvent>(
            Assert.Single(probe.Events, e => e.Name == AxiosTelemetry.RequestStartEventName).Payload);
        Assert.Equal(HttpMethod.Get, start.Method);
        Assert.Equal("/" + path, start.Uri.AbsolutePath);
        Assert.Same(response.Config, start.Config);

        var stop = Assert.IsType<AxiosRequestStopEvent>(
            Assert.Single(probe.Events, e => e.Name == AxiosTelemetry.RequestStopEventName).Payload);
        Assert.Equal(200, stop.Response.Status);
        Assert.True(stop.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Diagnostics_EmitFailed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        var path = UniquePath("events-failed");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + path));

        var failed = Assert.IsType<AxiosRequestFailedEvent>(
            Assert.Single(probe.Events, e => e.Name == AxiosTelemetry.RequestFailedEventName).Payload);

        Assert.Equal(AxiosException.ErrBadResponse, failed.ErrorCode);
        Assert.Equal(500, failed.Status);
        Assert.IsType<AxiosException>(failed.Exception);
        Assert.Empty(probe.Events.Where(e => e.Name == AxiosTelemetry.RequestStopEventName));
    }

    // ------------------------------------------------------------------
    // 配置：开关 / URL 采集 / 合并
    // ------------------------------------------------------------------

    [Fact]
    public async Task CaptureUrl_False_OmitsFullUrlButKeepsPath()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("no-url");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Telemetry = { CaptureUrl = false } });
        await client.GetAsync(server.Url + path + "?token=secret");

        var span = Assert.Single(probe.Spans);
        Assert.Null(span.GetTagItem(TagFullUrl));
        Assert.Equal("/" + path, span.GetTagItem(TagPath));
        Assert.Equal("127.0.0.1", span.GetTagItem(TagServerAddress));
    }

    [Fact]
    public async Task Default_OmitsQueryFromFullUrl()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("no-query");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        await client.GetAsync(server.Url + path + "?access_token=secret");

        // 默认不把查询串写进 span：token / 签名会随 span 上报到第三方后端
        var span = Assert.Single(probe.Spans);
        var fullUrl = Assert.IsType<string>(span.GetTagItem(TagFullUrl));
        Assert.Contains("/" + path, fullUrl);
        Assert.DoesNotContain("access_token", fullUrl);
        Assert.DoesNotContain("secret", fullUrl);
    }

    [Fact]
    public async Task CaptureQuery_True_IncludesQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("with-query");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Telemetry = { CaptureQuery = true } });
        await client.GetAsync(server.Url + path + "?access_token=secret");

        // 排障需要时显式打开
        var span = Assert.Single(probe.Spans);
        var fullUrl = Assert.IsType<string>(span.GetTagItem(TagFullUrl));
        Assert.Contains("access_token=secret", fullUrl);
    }

    [Fact]
    public async Task CaptureUrl_StripsCredentialsFromFullUrl()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("userinfo");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();

        // URL 里带 userinfo 时（等价于 https://user:pass@host/）：span 会进第三方后端，
        // 凭据在那儿既没用也不安全 —— url.full 必须是脱敏后的形态。
        var withCredentials = new UriBuilder(server.Url + path) { UserName = "user", Password = "s3cret" }.Uri;
        await client.GetAsync(withCredentials.AbsoluteUri);

        var span = Assert.Single(probe.Spans);
        var fullUrl = Assert.IsType<string>(span.GetTagItem(TagFullUrl));
        Assert.DoesNotContain("s3cret", fullUrl);
        Assert.DoesNotContain("user:", fullUrl);
        Assert.Contains("/" + path, fullUrl);

        // 转义形态不能被二次编码（%20 仍是 %20，不是 %2520）
        Assert.DoesNotContain("%25", fullUrl);
    }

    [Fact]
    public async Task Disabled_ProducesNoTelemetry_ButRequestStillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("disabled");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Telemetry = { Enabled = false } });
        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);
        Assert.Empty(probe.Spans);
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Empty(probe.Events);
    }

    [Fact]
    public async Task Disabled_AlsoSuppressesRetryTelemetry()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) <= 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        var path = UniquePath("disabled-retry");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(3, TimeSpan.Zero) },
            Telemetry = { Enabled = false }
        });

        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);
        Assert.Equal(2, calls); // 重试确实发生了：关掉的只是遥测

        // 重试是**独立于主路径**的一段观测（在 Attempt.RetryLoopAsync 里），漏判开关的话
        // span 与耗时指标都没了、偏偏重试计数和 Retry 事件还在 —— 指标会被这种请求悄悄污染。
        Assert.Empty(probe.Spans);
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RetriesInstrumentName));
        Assert.Empty(probe.Events);
    }

    [Fact]
    public async Task Disabled_StillRecordsPoolLeaseMetrics()
    {
        // 池域指标是「池这个资源」的视图，不是某一次请求的产出：租约 / 建节点 / 驱逐三者必须能互相对账
        // （命中率 = 1 - created / leases，当前节点数 = created - evicted）。按请求开关把它们切两半，
        // 会让这些除法在「探针请求关了遥测」的服务上系统性失真 —— 所以这里刻意**不**跟随 Telemetry.Enabled。
        // 改这段行为前先看这条用例：它存在的意义就是把「故意的」和「漏判的」区分开。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("disabled-pool");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(new AxiosConfig { Telemetry = { Enabled = false } });
        await client.GetAsync(server.Url + path);

        // 请求级三路一律没有
        Assert.Empty(probe.Spans);
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Empty(probe.Events);

        // 池域两路照旧
        Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolLeasesInstrumentName));
        Assert.Single(probe.MeasurementsOf(AxiosTelemetry.PoolEndpointsCreatedInstrumentName));
    }

    [Fact]
    public async Task Enabled_CanBeTurnedBackOnPerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("reenabled");
        using var probe = new TelemetryProbe(server.Port, path);

        // 实例默认关闭；单次请求显式打开（bool? 合并语义：null 才沿用实例值）
        using var client = new AxiosClient(new AxiosConfig { Telemetry = { Enabled = false } });
        await client.GetAsync(server.Url + path, configure: config => config.Telemetry.Enabled = true);

        Assert.Single(probe.Spans);
    }

    [Fact]
    public async Task Config_Clone_CopiesTelemetrySettings()
    {
        var config = new AxiosConfig { Telemetry = { Enabled = false, CaptureUrl = false } };
        var clone = config.Clone();

        Assert.False(clone.Telemetry.Enabled);
        Assert.False(clone.Telemetry.CaptureUrl);

        // 克隆是独立副本：改克隆不能影响源
        clone.Telemetry.Enabled = true;
        Assert.False(config.Telemetry.Enabled);
    }

    // ------------------------------------------------------------------
    // 隔离性：遥测出错不能影响请求
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThrowingActivityListener_DoesNotBreakRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("throwing-listener");

        // ActivitySource.StartActivity 会把 ActivityStarted 的异常原样抛出（Dispose 则吞掉）：
        // 库必须自己兜住，否则「监听者写错」= 「请求失败」。
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AxiosTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => throw new InvalidOperationException("listener boom")
        };
        ActivitySource.AddActivityListener(listener);

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ThrowingDiagnosticObserver_DoesNotBreakRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("throwing-observer");

        using var observer = new ThrowingDiagnosticObserver();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(observer);

        using var client = new AxiosClient();
        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);
    }

    private const string TagMethod = "http.request.method";

    private const string TagScheme = "url.scheme";

    private const string TagPath = "url.path";

    private const string TagFullUrl = "url.full";

    private const string TagServerAddress = "server.address";

    private const string TagServerPort = "server.port";

    private const string TagStatusCode = "http.response.status_code";

    private const string TagProtocolVersion = "network.protocol.version";

    private const string TagErrorType = "error.type";

    private const string TagResendCount = "http.request.resend_count";

    private const string TagRequestBodySize = "http.request.body.size";

    private const string TagResponseBodySize = "http.response.body.size";

    // 库自有标签（语义约定没有对应项）
    private const string TagErrorCode = "axiosnet.error.code";

    private const string TagEndpointReused = "axiosnet.pool.endpoint.reused";

    private const string TagEvictionReason = "axiosnet.pool.eviction.reason";

    /// <summary>每个用例独有的路径片段：并行用例之间的 span / 事件靠它区分。</summary>
    private static string UniquePath(string name) => $"telemetry/{name}-{Guid.NewGuid():N}";

    /// <summary>取一个刚被释放的本机端口：连它必然被拒，用于制造网络错误。</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 订阅「AxiosNet.DiagnosticListener」后每条事件都抛。<b>内层订阅必须自己退订</b>：
    /// 不退的话本进程里 <c>DiagnosticListener.IsEnabled</c> 从此恒为 true ——
    /// 后面的用例（尤其性能护栏）会为一个没人要的事件白付载荷分配，还得每次撞一次异常。
    /// </summary>
    private sealed class ThrowingDiagnosticObserver : IObserver<DiagnosticListener>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == AxiosTelemetry.DiagnosticListenerName)
            {
                _subscriptions.Add(value.Subscribe(new Thrower()));
            }
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        private sealed class Thrower : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(KeyValuePair<string, object?> value)
                => throw new InvalidOperationException("observer boom");
        }
    }
}