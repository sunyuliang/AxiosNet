using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AxiosNet.Retry;
using AxiosNet.Telemetry;

namespace AxiosNet.Tests.Infrastructure;

/// <summary>一次指标测量（instrument 名 + 值 + 标签快照）。</summary>
internal sealed record TelemetryMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    public object? Tag(string name) => Tags.TryGetValue(name, out var value) ? value : null;
}

/// <summary>一条诊断事件（事件名 + 载荷）。</summary>
internal sealed record TelemetryEvent(string Name, object Payload);

/// <summary>
/// 三路观测点的收集器：<c>ActivitySource</c>（span）/ <c>Meter</c>（指标）/ <c>DiagnosticListener</c>（事件）。
/// <para>
/// <b>为什么要过滤</b>：xunit 的测试类之间并行执行，同一进程里别的用例也在发请求、也在开 span，
/// 所以收集器只收「属于本用例」的数据 —— span 与事件按 <c>url.path</c> / 请求 URL 里的路径过滤，
/// 指标按 <c>server.port</c> 标签过滤（重试计数与耗时直方图都带这两个维度）。
/// </para>
/// </summary>
internal sealed class TelemetryProbe : IDisposable
{
    /// <summary>
    /// 不带 <c>server.port</c> 的指标：池 / 闸门 / 熔断 / 重试预算这几组刻意低基数（不按 host 打标签），
    /// 没法按端口筛。它们只在独占的 <c>telemetry</c> 集合里断言，靠集合禁并行隔离。
    /// </summary>
    private static readonly HashSet<string> UntaggedInstruments = new(StringComparer.Ordinal)
    {
        AxiosTelemetry.PoolLeasesInstrumentName,
        AxiosTelemetry.PoolEndpointsCreatedInstrumentName,
        AxiosTelemetry.PoolEndpointsEvictedInstrumentName,
        AxiosTelemetry.GateQueueDepthInstrumentName,
        AxiosTelemetry.GateRejectionsInstrumentName,
        AxiosTelemetry.CircuitStateInstrumentName,
        AxiosTelemetry.RetryBudgetExhaustedInstrumentName,
        AxiosTelemetry.CacheHitsInstrumentName,
        AxiosTelemetry.CacheMissesInstrumentName,
        AxiosTelemetry.CacheStoresInstrumentName,
        AxiosTelemetry.CacheStoreFailuresInstrumentName
    };

    private readonly string _marker;
    private readonly int _port;
    private readonly object _sync = new();
    private readonly List<Activity> _spans = new();
    private readonly List<TelemetryMeasurement> _measurements = new();
    private readonly List<TelemetryEvent> _events = new();

    /// <summary>对「AxiosNet.DiagnosticListener」本身的订阅，<see cref="Dispose"/> 时必须逐个退订。</summary>
    private readonly List<IDisposable> _eventSubscriptions = new();

    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly IDisposable _listenersSubscription;
    private bool _disposed;

    /// <param name="port">测试服务器端口：用于筛掉并行的其它用例。</param>
    /// <param name="path">本次请求独有的路径片段（如 <c>telemetry/abc123</c>）。</param>
    public TelemetryProbe(int port, string path)
    {
        _port = port;
        _marker = "/" + path.TrimStart('/');

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AxiosTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Matches(activity.GetTagItem("url.path") as string))
                {
                    lock (_sync)
                    {
                        _spans.Add(activity);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AxiosTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _meterListener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _meterListener.Start();

        _listenersSubscription = DiagnosticListener.AllListeners.Subscribe(new CallbackObserver<DiagnosticListener>(listener =>
        {
            if (listener.Name != AxiosTelemetry.DiagnosticListenerName)
            {
                return;
            }

            var subscription = listener.Subscribe(new CallbackObserver<KeyValuePair<string, object?>>(pair =>
            {
                if (MatchesPayload(pair.Value))
                {
                    lock (_sync)
                    {
                        _events.Add(new TelemetryEvent(pair.Key, pair.Value!));
                    }
                }
            }));

            // 内层订阅必须自己退订：AllListeners 的订阅只管「发现监听器」，不退订内层的话
            // 本进程里会一直留着订阅者 —— DiagnosticListener.IsEnabled 从此恒为 true，
            // 后续用例（尤其性能护栏）就得为一个没人要的事件白付载荷分配。
            lock (_sync)
            {
                if (_disposed)
                {
                    subscription.Dispose();
                }
                else
                {
                    _eventSubscriptions.Add(subscription);
                }
            }
        }));
    }

    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_sync)
            {
                return _spans.ToArray();
            }
        }
    }

    public IReadOnlyList<TelemetryMeasurement> Measurements
    {
        get
        {
            lock (_sync)
            {
                return _measurements.ToArray();
            }
        }
    }

    public IReadOnlyList<TelemetryEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>某个 instrument 的全部测量（已按本用例过滤）。</summary>
    public IReadOnlyList<TelemetryMeasurement> MeasurementsOf(string instrument)
        => Measurements.Where(m => m.Instrument == instrument).ToArray();

    /// <summary>主动触发一次可观测 instrument 的收集（ObservableGauge 只在收集时回调）。</summary>
    public void CollectObservable() => _meterListener.RecordObservableInstruments();

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
        _listenersSubscription.Dispose();

        lock (_sync)
        {
            _disposed = true;
            foreach (var subscription in _eventSubscriptions)
            {
                subscription.Dispose();
            }

            _eventSubscriptions.Clear();
        }
    }

    private void OnDoubleMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, value, tags);

    private void OnLongMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, value, tags);

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // 先做一次零分配的端口匹配：这个回调跑在请求线程上，给「不属于本用例」的测量建字典会白花分配
        var matched = false;
        foreach (var tag in tags)
        {
            if (tag.Key == "server.port" && tag.Value is int port && port == _port)
            {
                matched = true;
                break;
            }
        }

        // 没有 server.port 的测量：只有池 / 闸门 / 熔断这几组低基数指标收（见 UntaggedInstruments），
        // 全局水位（在途数等）不属于任何用例，直接丢掉。
        if (!matched && !UntaggedInstruments.Contains(instrument.Name))
        {
            return;
        }

        var map = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        lock (_sync)
        {
            _measurements.Add(new TelemetryMeasurement(instrument.Name, value, map));
        }
    }

    private bool Matches(string? urlOrPath)
        => urlOrPath is not null && urlOrPath.Contains(_marker, StringComparison.Ordinal);

    private bool MatchesPayload(object? payload) => payload switch
    {
        AxiosRequestStartEvent start => Matches(start.Uri.AbsolutePath),
        AxiosRequestStopEvent stop => Matches(stop.Uri.AbsolutePath),
        AxiosRequestFailedEvent failed => Matches(failed.Uri.AbsolutePath),
        AxiosAttemptEvent attempt => Matches(attempt.Uri.AbsolutePath),
        // 重试事件没有 Uri：按配置里的请求地址匹配（测试都用 server.Url + path 的绝对地址）
        AxiosRetryContext retry => Matches(retry.Config?.Url),
        _ => false
    };

    private sealed class CallbackObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public CallbackObserver(Action<T> onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => _onNext(value);
    }
}
