using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 上传/下载共用的流量计量与进度上报：累计字节、按 50ms 节流回调、按 MaxRate 反算应等待的时间。
/// </summary>
internal sealed class TrafficMeter
{
    private const int ReportIntervalMs = 50;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;

    private long _loaded;

    /// <summary>本轮传输的起点（累计值快照）：完成判定按本轮增量算，重发正文时由 <see cref="Restart"/> 推进。</summary>
    private long _roundStart;

    /// <summary>本轮起点的时钟快照：速率与限速按本轮增量与本轮耗时算。</summary>
    private TimeSpan _roundStartClock;

    /// <summary>
    /// 本轮是否已定起点：由首字节到达时惰性确定，而不是计量器构造时。
    /// 否则「响应已就绪 → 调用方稍后才开始读」的这段间隔会被当成已消耗的限速预算，
    /// 停顿越久限速越弱（间隔超过整段应耗时后限速等于没有）。
    /// </summary>
    private bool _roundStarted;

    private long _lastReportMs = -ReportIntervalMs;
    private bool _completed;

    public TrafficMeter(long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _total = total;
        _callback = callback;
        _maxRate = maxRate;
    }

    public long Loaded => _loaded;

    /// <summary>累计本轮字节；首字节在此定下本轮计时起点（见 <see cref="_roundStarted"/>）。</summary>
    public void Add(int count)
    {
        if (!_roundStarted)
        {
            BeginRound();
        }

        _loaded += count;
    }

    /// <summary>按平均速率反算「当前应耗时 - 实际耗时」；不限速时返回零。</summary>
    public TimeSpan ComputeDelay()
    {
        if (_maxRate is not long rate || rate <= 0)
        {
            return TimeSpan.Zero;
        }

        var bytes = _loaded - _roundStart;
        var elapsed = _clock.Elapsed - _roundStartClock;
        var expected = TimeSpan.FromSeconds((double)bytes / rate);
        var delta = expected - elapsed;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }

    public void Report(bool force, bool upload)
    {
        if (_callback is null || _completed)
        {
            return;
        }

        // 读满本轮声明长度时必须立即上报，不受 50ms 节流，否则 100% 可能被吞掉。
        if (_total is long declared && declared > 0 && _loaded - _roundStart >= declared)
        {
            force = true;
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
        _callback(BuildEvent(upload));

        // 已传满本轮声明长度：后续 EOF / Complete 不再重复报 100%。
        // total 必须大于 0，否则 0 >= 0 恒真，第一次上报就会锁死计量器。
        if (_total is long total && total > 0 && _loaded - _roundStart >= total)
        {
            _completed = true;
        }
    }

    /// <summary>完成时强制上报一次。</summary>
    public void Complete(bool upload) => Report(force: true, upload);

    /// <summary>开始新一次实际传输（重发正文时调用）：只解除「已完成」状态，并把本轮起点推进到此刻。</summary>
    public void Restart()
    {
        _completed = false;
        BeginRound();
    }

    /// <summary>把本轮起点钉在此刻：累计值快照 + 时钟快照，同时标记本轮已开始计时。</summary>
    private void BeginRound()
    {
        _roundStarted = true;
        _roundStart = _loaded;
        _roundStartClock = _clock.Elapsed;
    }

    private AxiosProgressEvent BuildEvent(bool upload)
    {
        // 只按本轮增量算，否则重发时百分比会超过 100%。
        var roundBytes = _loaded - _roundStart;
        var seconds = (_clock.Elapsed - _roundStartClock).TotalSeconds;
        double? rate = seconds > 0 && roundBytes > 0 ? roundBytes / seconds : null;

        // Loaded 是累计发送量，保持单调推进；Progress 自身封顶 1。
        var loaded = _loaded;

        TimeSpan? estimated = rate is > 0 && _total is long total && total > roundBytes
            ? TimeSpan.FromSeconds((total - roundBytes) / rate.Value)
            : null;

        return new AxiosProgressEvent(loaded, _total, rate, estimated, upload);
    }
}

/// <summary>
/// 下载侧流量包装：统计已读字节、回报进度、按 MaxRate 限速，并在累计字节超出 MaxContentLength 时中断。
/// </summary>
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

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        ThrowIfOverLimit(read);
        await TrackAsync(read, cancellationToken).ConfigureAwait(false);
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
            throw AxiosException.MaxContentLength(_config, _request, total, limit);
        }
    }

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

    /// <summary>回报进度：回调异常统一成 <c>ERR_BAD_RESPONSE</c>，原始异常放在 Cause 里。</summary>
    private void Report(bool force)
    {
        try
        {
            _meter.Report(force, upload: false);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.ProgressCallbackFailed(_config, _request, nameof(AxiosConfig.CallbackSettings.OnDownloadProgress), ex, upload: false);
        }
    }
}

/// <summary>
/// 上传侧流量包装：写入底层传输流时统计字节、回报进度并限速。
/// 底层流由 HttpClient 拥有，因此默认不随本包装释放（leaveOpen）。
/// </summary>
internal sealed class ProgressWriteStream : DelegatingStream
{
    private readonly TrafficMeter _meter;

    /// <summary>进度回调抛异常时的错误翻译（由内容层提供带配置上下文的统一异常）。</summary>
    private readonly Func<Exception, Exception> _mapCallbackError;

    /// <summary>计量器由外部提供：同一个 <see cref="HttpContent"/> 重发正文时共用同一个计量器。</summary>
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override bool DisposeInnerStream => false;

    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TrackAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TrackAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        _meter.Add(count);

        var delay = _meter.ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _meter.Report(force: false, upload: true);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            // 回调异常必须换成统一错误模型，否则会被包成 HttpRequestException 再误报为 ERR_NETWORK。
            throw _mapCallbackError(ex);
        }
    }
}
