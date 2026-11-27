using System.Diagnostics;

namespace AxiosNet.Internal.Response;

/// <summary>上传 / 下载共用的流量计量与进度上报：累计字节、按 50 ms 节流回调、按 MaxRate 反算应等待的时间。</summary>
/// <remarks><see cref="Loaded"/> 与 <see cref="Add"/> 可并发写，不丢字节。</remarks>
internal sealed class TrafficMeter
{
    // 进度回调的最小间隔。
    private const int ReportIntervalMs = 50;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;

    private long _loaded;

    /// <summary>本轮传输的起点（累计值快照），<see cref="Restart"/> 时推进。</summary>
    private long _roundStart;

    /// <summary>本轮起点的时钟快照。</summary>
    private TimeSpan _roundStartClock;

    /// <summary>本轮起点是否已确定（首字节到达时惰性确定）。</summary>
    private int _roundStartedState;

    // 上次回调时刻；负初值保证首块一定上报。
    private long _lastReportMs = -ReportIntervalMs;

    // 上次回调时的累计值：-1 保证首帧（0 字节也算）一定能发出去。
    private long _lastReportedLoaded = -1;
    // 完成回调只发一次。
    private bool _completed;

    public TrafficMeter(long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _total = total;
        _callback = callback;
        _maxRate = maxRate;
    }

    /// <summary>累计已传输字节（跨重发轮次）。</summary>
    public long Loaded => Volatile.Read(ref _loaded);

    /// <summary>累计本轮字节；首字节在此定下本轮计时起点。</summary>
    public void Add(int count)
    {
        if (Volatile.Read(ref _roundStartedState) == 0)
        {
            BeginRound();
        }

        Interlocked.Add(ref _loaded, count);
    }

    /// <summary>是否配置了限速。</summary>
    public bool IsThrottled => _maxRate is long rate && rate > 0;

    /// <summary>按平均速率反算「当前应耗时 - 实际耗时」；不限速时返回零。</summary>
    /// <param name="upcoming">尚未计入 <see cref="Loaded"/> 的字节数。</param>
    public TimeSpan ComputeDelay(int upcoming = 0)
    {
        if (_maxRate is not long rate || rate <= 0)
        {
            return TimeSpan.Zero;
        }

        var bytes = Volatile.Read(ref _loaded) - _roundStart + upcoming;
        var elapsed = _clock.Elapsed - _roundStartClock;
        var expected = TimeSpan.FromSeconds((double)bytes / rate);
        var delta = expected - elapsed;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }

    /// <summary>限速时一次 I/O 最多搬运的字节数（约一个上报窗口的流量）；不限速时返回 <paramref name="remaining"/>。</summary>
    public int SliceLength(int remaining)
    {
        if (remaining <= 0)
        {
            return 0;
        }

        if (_maxRate is not long rate || rate <= 0)
        {
            return remaining;
        }

        // 每片覆盖一个上报窗口。
        var perWindow = (long)(rate * (ReportIntervalMs / 1000.0));
        if (perWindow < 1)
        {
            perWindow = 1;
        }

        return perWindow < remaining ? (int)perWindow : remaining;
    }

    /// <summary>节流上报进度；force 或本轮已传满声明长度时立即上报并置「已完成」。</summary>
    /// <remarks>累计值没推进时不再重复上报：EOF / 收尾的强制上报只为「最后一帧一定发出去」，不该造出同值的第二条事件。</remarks>
    public void Report(bool force, bool upload)
    {
        if (_callback is null || _completed)
        {
            return;
        }

        // 读满声明长度时不受节流。
        if (_total is long declared && declared > 0 && Volatile.Read(ref _loaded) - _roundStart >= declared)
        {
            force = true;
        }

        var loaded = Volatile.Read(ref _loaded);
        if (force && loaded == _lastReportedLoaded)
        {
            return;
        }

        var elapsedMs = (long)_clock.Elapsed.TotalMilliseconds;
        if (!force && elapsedMs - _lastReportMs < ReportIntervalMs)
        {
            return;
        }

        if (force)
        {
            _completed = true;
        }

        _lastReportMs = elapsedMs;
        _lastReportedLoaded = loaded;
        _callback(BuildEvent(upload));

        // 已传满：后续不再重复报 100%。
        if (_total is long total && total > 0 && loaded - _roundStart >= total)
        {
            _completed = true;
        }
    }

    /// <summary>完成时强制上报一次。</summary>
    public void Complete(bool upload) => Report(force: true, upload);

    /// <summary>开始新一次实际传输（重发正文时调用）：解除「已完成」并把本轮起点推进到此刻。</summary>
    /// <remarks>本轮至少要多发一条事件（哪怕是 0 字节的空正文重发），所以同时清掉「上次上报值」。</remarks>
    public void Restart()
    {
        _completed = false;
        _lastReportedLoaded = -1;
        BeginRound();
    }

    /// <summary>把本轮起点钉在此刻（累计值与时钟快照）。</summary>
    private void BeginRound()
    {
        // 先钉累计值再标记开始。
        _roundStart = Volatile.Read(ref _loaded);
        _roundStartClock = _clock.Elapsed;
        Volatile.Write(ref _roundStartedState, 1);
    }

    // 进度事件按本轮增量计算。
    private AxiosProgressEvent BuildEvent(bool upload)
    {
        var roundBytes = Volatile.Read(ref _loaded) - _roundStart;
        var seconds = (_clock.Elapsed - _roundStartClock).TotalSeconds;
        double? rate = seconds > 0 && roundBytes > 0 ? roundBytes / seconds : null;

        // Loaded 保持单调推进。
        var loaded = Volatile.Read(ref _loaded);

        TimeSpan? estimated = rate is > 0 && _total is long total && total > roundBytes
            ? TimeSpan.FromSeconds((total - roundBytes) / rate.Value)
            : null;

        return new AxiosProgressEvent(loaded, _total, rate, estimated, upload);
    }
}

/// <summary>下载侧流量包装：统计已读字节、回报进度、按 MaxRate 限速，超出 Limits.MaxResponseBytes 时中断。</summary>
internal sealed class ProgressReadStream : DelegatingStream
{
    private readonly TrafficMeter _meter;
    private readonly long? _contentLimit;
    private readonly AxiosConfig _config;
    private readonly HttpRequestMessage _request;

    public ProgressReadStream(
        Stream inner,
        long? total,
        Action<AxiosProgressEvent>? callback,
        long? maxRate,
        long? contentLimit,
        AxiosConfig config,
        HttpRequestMessage request)
        : base(inner)
    {
        _meter = new TrafficMeter(total, callback, maxRate);
        _contentLimit = contentLimit;
        _config = config;
        _request = request;
    }

    public override bool CanRead => true;

    public override bool CanWrite => false;

    // 读取流不能改长度。
    public override void SetLength(long value) => throw new NotSupportedException();

    // 不支持写。
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>读一块并记账：内层同步完成走快路径，异步等待或需限速时才走异步路径。</summary>
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // 限速时按窗口切片读，只切 Memory 视图，不复制数据。
        var request = _meter.SliceLength(buffer.Length) is var slice && slice < buffer.Length
            ? buffer.Slice(0, slice)
            : buffer;

        var pending = Inner.ReadAsync(request, cancellationToken);

        // 异步与同步故障都走异步路径。
        if (!pending.IsCompletedSuccessfully)
        {
            return ReadSlowAsync(pending, cancellationToken);
        }

        var read = pending.Result;
        ThrowIfOverLimit(read);

        if (read <= 0)
        {
            Report(force: true);
            return new ValueTask<int>(read);
        }

        _meter.Add(read);

        var delay = _meter.ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            return DelayThenReportAsync(delay, read, cancellationToken);
        }

        Report(force: false);
        return new ValueTask<int>(read);
    }

    /// <summary>内层未同步完成时的读路径，语义与快路径一致。</summary>
    private async ValueTask<int> ReadSlowAsync(ValueTask<int> pending, CancellationToken cancellationToken)
    {
        var read = await pending.ConfigureAwait(false);
        ThrowIfOverLimit(read);
        await TrackAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    /// <summary>先睡够「应耗时 - 实际耗时」再上报进度。</summary>
    private async ValueTask<int> DelayThenReportAsync(TimeSpan delay, int read, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        Report(force: false);
        return read;
    }

    /// <summary>累计字节越界即中断，覆盖 chunked / 未声明长度的流式响应。</summary>
    private void ThrowIfOverLimit(int read)
    {
        if (read <= 0 || _contentLimit is not long limit)
        {
            return;
        }

        var total = _meter.Loaded + read;
        if (total > limit)
        {
            throw AxiosException.MaxResponseBytes(_config, _request, total, limit);
        }
    }

    // 读侧记账：结尾强制上报一次，保证最后一帧进度一定发出。
    private async ValueTask TrackAsync(int read, CancellationToken cancellationToken)
    {
        if (read <= 0)
        {
            Report(force: true);
            return;
        }

        _meter.Add(read);

        var delay = _meter.ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        Report(force: false);
    }

    /// <summary>回报进度；回调异常统一成 <c>ERR_BAD_RESPONSE</c>，原始异常放在 Cause 里。</summary>
    private void Report(bool force)
    {
        try
        {
            _meter.Report(force, upload: false);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.CallbackFailed(_config, _request, nameof(AxiosConfig.ObservabilitySettings.OnDownloadProgress), ex, CallbackStage.Response);
        }
    }
}

/// <summary>上传侧流量包装：写入底层流时统计字节、回报进度并限速；底层流由 HttpClient 拥有，不随本包装释放。</summary>
internal sealed class ProgressWriteStream : DelegatingStream
{
    private readonly TrafficMeter _meter;

    /// <summary>进度回调异常的翻译函数。</summary>
    private readonly Func<Exception, Exception> _mapCallbackError;

    /// <summary>计量器由外部提供：同一 <see cref="HttpContent"/> 重发正文时共用。</summary>
    public ProgressWriteStream(Stream inner, TrafficMeter meter, Func<Exception, Exception> mapCallbackError)
        : base(inner)
    {
        _meter = meter;
        _mapCallbackError = mapCallbackError;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _meter.Loaded;
        set => throw new NotSupportedException();
    }

    // 不支持定位。
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    // 不支持改长度。
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override bool DisposeInnerStream => false;

    // 不支持读。
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    // 限速时按窗口切片写，只切只读视图，零分配。
    protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        // 空写直接返回，与切片路径行为一致。
        => buffer.IsEmpty
            ? default
            : _meter.IsThrottled
                ? WriteThrottledAsync(buffer, cancellationToken)
                : WriteAllAsync(buffer, cancellationToken);

    /// <summary>不限速的快路径：整块一次写完再记账。</summary>
    /// <remarks>顺序与慢路径一致：先写内层再记账。</remarks>
    private async ValueTask WriteAllAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _meter.Add(buffer.Length);
        ReportUpload();
    }

    /// <summary>限速路径：按上报窗口切片，每片等到应发时刻再写，写后记账并回报进度。</summary>
    private async ValueTask WriteThrottledAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = _meter.SliceLength(buffer.Length - offset);
            var delay = _meter.ComputeDelay(count);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            // 先写内层再记账。
            await Inner.WriteAsync(buffer.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            _meter.Add(count);
            offset += count;
            ReportUpload();
        }
    }

    /// <summary>回报上传进度；回调异常换成统一错误模型。</summary>
    private void ReportUpload()
    {
        try
        {
            _meter.Report(force: false, upload: true);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw _mapCallbackError(ex);
        }
    }
}
