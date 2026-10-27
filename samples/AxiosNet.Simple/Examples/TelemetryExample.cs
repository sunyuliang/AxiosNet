using System.Diagnostics;
using System.Diagnostics.Metrics;
using AxiosNet;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 27. 可观测性：ActivitySource（链路）/ Meter（指标）/ DiagnosticListener（事件）。
/// <para>
/// 三路观测点默认就开着，但「有人订阅才产出」：本示例现场订阅三路，于是能看到一次请求的完整观测数据；
/// 最后再演示 <c>Telemetry.Enabled = false</c>（健康检查这类请求不想污染指标）与 <c>CaptureUrl</c>
/// （查询串里有令牌时不记录完整 URL）。
/// </para>
/// <para>
/// 接 OpenTelemetry 时不用这些手工订阅（示例项目不引 OTel 包，这里只写出来）：两行订阅 + 一个 View。
/// View 不是可选项 —— 耗时直方图按语义约定用<b>秒</b>，而 OTel .NET 的默认分桶边界是按毫秒设计的
/// （0, 5, 10, 25 … 10000），不配的话正常请求会全落进 (0, 5] 一个桶，5 ms 和 4 s 分不开。
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource("AxiosNet"))
///     .WithMetrics(m =&gt; m
///         .AddMeter("AxiosNet")
///         .AddView("axiosnet.client.request.duration", new ExplicitBucketHistogramConfiguration
///         {
///             Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
///         }));
/// </code>
/// </para>
/// </summary>
public sealed class TelemetryExample : IExample
{
    /// <summary>带重试策略的客户端：让重试事件与重试计数有东西可看。</summary>
    private static AxiosClient? _client;

    public int Number => 27;

    public string Title => "可观测性：span / 指标 / 诊断事件，以及按请求关掉遥测";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var client = _client ??= new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            Retry = { Policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(100)) }
        });

        // ── 1) 链路：订阅 ActivitySource「AxiosNet」 ────────────────────────────
        // 一次逻辑请求 = 一个 span（重试不另开 span，次数记在 http.request.resend_count 上）。
        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AxiosTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        // ── 2) 指标：订阅 Meter「AxiosNet」 ────────────────────────────────────
        var metrics = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AxiosTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (instrument.Name == AxiosTelemetry.RequestDurationInstrumentName)
            {
                lock (metrics)
                {
                    metrics.Add($"{instrument.Name} = {value:0.000}s [{Tags(tags)}]");
                }
            }
        });
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (metrics)
            {
                metrics.Add($"{instrument.Name} += {value} [{Tags(tags)}]");
            }
        });
        meterListener.Start();

        // ── 3) 事件：订阅 DiagnosticListener「AxiosNet.DiagnosticListener」 ─────
        var events = new List<string>();
        IDisposable? eventSubscription = null;
        using var listenerSubscription = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name != AxiosTelemetry.DiagnosticListenerName)
            {
                return;
            }

            eventSubscription = listener.Subscribe(new Observer<KeyValuePair<string, object?>>(pair =>
            {
                lock (events)
                {
                    events.Add(pair.Key switch
                    {
                        AxiosTelemetry.RequestStartEventName => $"{pair.Key}: {((AxiosRequestStartEvent)pair.Value!).Method} {((AxiosRequestStartEvent)pair.Value!).Uri.AbsolutePath}",
                        AxiosTelemetry.RequestStopEventName => $"{pair.Key}: {((AxiosRequestStopEvent)pair.Value!).Response.Status} 用时 {((AxiosRequestStopEvent)pair.Value!).Elapsed.TotalMilliseconds:0}ms",
                        AxiosTelemetry.RequestFailedEventName => $"{pair.Key}: {((AxiosRequestFailedEvent)pair.Value!).ErrorCode}",
                        AxiosTelemetry.RetryEventName => $"{pair.Key}: 第 {((AxiosRetryContext)pair.Value!).Attempt} 次尝试失败（{((AxiosRetryContext)pair.Value!).ErrorCode}），{((AxiosRetryContext)pair.Value!).Delay.TotalMilliseconds:0}ms 后重试",
                        _ => pair.Key
                    });
                }
            }));
        }));

        Console.WriteLine("  已订阅三路观测点：ActivitySource / Meter / DiagnosticListener");
        Console.WriteLine();

        // 1) 成功请求：503 前两次、第三次成功（演示重试如何出现在观测数据里）
        var tag = Guid.NewGuid().ToString("N")[..8];
        var response = await client.GetAsync<object>($"flaky?fail=2&tag={tag}");
        Console.WriteLine($"  [1] GET /flaky（前两次 503，第三次成功）-> status={response.Status}，协议={response.ProtocolVersion}");

        // 2) 失败请求：4xx 直接抛错，span 上是 Error + error.type
        try
        {
            await client.GetAsync<object>("not-found");
        }
        catch (AxiosException)
        {
            // 预期：演示失败请求的观测数据
        }

        // 3) 关掉遥测：健康检查 / 探针请求不该进业务指标
        await client.GetAsync<object>("users/1", configure: config => config.Telemetry.Enabled = false);
        Console.WriteLine("  [2] 同上失败请求 + 一次 Telemetry.Enabled = false 的请求（不产出任何遥测）");

        meterListener.RecordObservableInstruments();
        await Task.Delay(50);

        Console.WriteLine();
        Console.WriteLine("  span（ActivitySource「AxiosNet」）：");
        lock (spans)
        {
            foreach (var span in spans)
            {
                Console.WriteLine($"    · {span.DisplayName}  {span.Duration.TotalMilliseconds:0.0}ms  status={span.Status}");
                foreach (var name in new[] { "url.path", "http.response.status_code", "network.protocol.version", "http.request.resend_count", "error.type" })
                {
                    if (span.GetTagItem(name) is { } value)
                    {
                        Console.WriteLine($"        {name} = {value}");
                    }
                }

                foreach (var retry in span.Events.Where(e => e.Name == "retry"))
                {
                    Console.WriteLine($"        event retry: resend_count={retry.Tags.First(t => t.Key == "http.request.resend_count").Value}");
                }
            }

            Console.WriteLine($"    共 {spans.Count} 个 span：三次请求里有一次被显式关掉，所以只看到两个");
        }

        Console.WriteLine();
        Console.WriteLine("  指标（Meter「AxiosNet」）：");
        lock (metrics)
        {
            foreach (var line in metrics)
            {
                Console.WriteLine("    · " + line);
            }
        }

        Console.WriteLine();
        Console.WriteLine("  事件（DiagnosticListener「AxiosNet.DiagnosticListener」）：");
        lock (events)
        {
            foreach (var line in events)
            {
                Console.WriteLine("    · " + line);
            }
        }

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 三路都默认开启，但「有人订阅才产出」：没人听时不建 span、不发事件，指标退化为几次空检查");
        Console.WriteLine("    · span 名按语义约定取方法（GET / POST），URL 放标签；标签用 OpenTelemetry HTTP semconv 标准名，后端不用额外映射");
        Console.WriteLine("    · 一次逻辑请求一个 span：重试次数记在 http.request.resend_count，每次重试再加一条 retry 事件");
        Console.WriteLine("    · 底层那次真实 I/O 的 span 由 BCL 自己产出（.NET 8+ 的 System.Net.Http），会自然挂在本库 span 下面；.NET 6 上 BCL 不产出，只剩本库这一个 span");
        Console.WriteLine("    · 接 OpenTelemetry：AddSource / AddMeter 订阅，再给耗时直方图配一个 View（秒单位 vs OTel 默认的毫秒边界），详见 README");
        Console.WriteLine("    · 耗时直方图的标签只有方法 / 主机 / 端口 / 状态码 / 错误类型，都是低基数维度，不会把指标打爆");
        Console.WriteLine("    · Telemetry.Enabled = false：健康检查、探针请求不进业务指标；url.full 默认不含查询串（令牌不会进遥测），确需查询串时显式设 CaptureQuery = true");
        Console.WriteLine("    · 遥测绝不影响请求：订阅方回调抛异常会被兜住（实测 ActivitySource 会把 ActivityStarted 的异常往外抛，库自己接了）");

        // 内层订阅同样要退订：菜单里后续示例还在同一进程跑，留着订阅会让它们白付遥测开销。
        eventSubscription?.Dispose();
    }

    private static string Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => string.Join(", ", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"));

    /// <summary>把委托包成 <see cref="IObserver{T}"/>（DiagnosticListener 的订阅接口）。</summary>
    private sealed class Observer<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public Observer(Action<T> onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => _onNext(value);
    }
}
