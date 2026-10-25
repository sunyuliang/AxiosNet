using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using AxiosNet.Retry;
using AxiosNet.Telemetry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 遥测用例的收集定义：<b>禁止与其它集合并行</b>。
/// <para>
/// 原因：观测点是进程级的 —— 用例里注册的 <see cref="ActivityListener"/> / <c>MeterListener</c> /
/// <c>DiagnosticListener</c> 订阅会让<b>同一进程里所有</b> AxiosNet 请求都开始产出 span / 指标 / 事件，
/// 而并行执行的分配护栏用例（如 <c>HeadersCopyOnWriteTests</c>）正是按「每请求分配字节数」断言的。
/// 让这组用例独占运行，既不影响别人，也不被别人的请求干扰。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "telemetry";
}

/// <summary>
/// 可观测性：ActivitySource（span）、Meter（指标）、DiagnosticListener（事件）三路观测点，
/// 以及「开关 / URL 采集 / 遥测不影响请求结果」这些契约。
/// <para>
/// 全部用真实的 <see cref="ActivityListener"/> / <see cref="System.Diagnostics.Metrics.MeterListener"/> /
/// <see cref="DiagnosticListener"/> 观察，不 mock 框架本身 —— 要验证的正是「框架真的能看到」。
/// 过滤规则见 <see cref="TelemetryProbe"/>（同进程里可能还有别的请求）。
/// </para>
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryTests
{
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

        Assert.Equal(3, instruments.Count);   // 指标数量也是契约：新增一个就要同步双版 README

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

        // 重试是**独立于主路径**的一段观测（在 SendWithRetryAsync 里），漏判开关的话
        // span 与耗时指标都没了、偏偏重试计数和 Retry 事件还在 —— 指标会被这种请求悄悄污染。
        Assert.Empty(probe.Spans);
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RequestDurationInstrumentName));
        Assert.Empty(probe.MeasurementsOf(AxiosTelemetry.RetriesInstrumentName));
        Assert.Empty(probe.Events);
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

        // 实测 ActivitySource.StartActivity 会把 ActivityStarted 的异常原样抛出（Dispose 则吞掉）：
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
