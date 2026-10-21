using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 上传/下载共用的流量计量与进度上报：
/// 累计字节、按 50ms 节流回调、按 <see cref="AxiosConfig.MaxRate"/> 反算应等待的时间。
/// </summary>
internal sealed class TrafficMeter
{
    private const int ReportIntervalMs = 50;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;

    private long _loaded;
    private long _lastReportMs = -ReportIntervalMs;
    private bool _completed;

    public TrafficMeter(long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _total = total;
        _callback = callback;
        _maxRate = maxRate;
    }

    public long Loaded => _loaded;

    public void Add(int count) => _loaded += count;

    /// <summary>按平均速率反算「当前应耗时 - 实际耗时」；不限速时返回零。</summary>
    public TimeSpan ComputeDelay()
    {
        if (_maxRate is not long rate || rate <= 0)
        {
            return TimeSpan.Zero;
        }

        var expected = TimeSpan.FromSeconds((double)_loaded / rate);
        var delta = expected - _clock.Elapsed;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }

    public void Report(bool force, bool upload)
    {
        if (_callback is null || _completed)
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
        _callback(BuildEvent(upload));

        // 已经传满声明长度：后续 EOF / Complete 不再重复报一次 100%
        if (_total is long total && _loaded >= total)
        {
            _completed = true;
        }
    }

    /// <summary>完成时强制上报一次（loaded == total）。</summary>
    public void Complete(bool upload) => Report(force: true, upload);

    private AxiosProgressEvent BuildEvent(bool upload)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        double? rate = seconds > 0 && _loaded > 0 ? _loaded / seconds : null;
        TimeSpan? estimated = rate is > 0 && _total is long total && total > _loaded
            ? TimeSpan.FromSeconds((total - _loaded) / rate.Value)
            : null;

        return new AxiosProgressEvent(_loaded, _total, rate, estimated, upload);
    }
}

/// <summary>
/// 下载侧流量包装：统计已读字节、按 <see cref="AxiosConfig.OnDownloadProgress"/> 回报进度、
/// 按 <see cref="AxiosConfig.MaxRate"/> 限速（读满配额后按速率等待），
/// 并在累计字节超出 <see cref="AxiosConfig.MaxContentLength"/> 时立刻中断。
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

    /// <summary>
    /// 累计字节越界即中断：既覆盖 chunked / 未声明长度的流式响应，
    /// 也让非流式路径在「超限的那一个缓冲区」上就停下，而不是等整个正文读完。
    /// </summary>
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
            _meter.Report(force: true, upload: false);
            return;
        }

        _meter.Add(read);

        var delay = _meter.ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        _meter.Report(force: false, upload: false);
    }
}

/// <summary>
/// 上传侧流量包装：写入底层传输流时统计字节、回报进度并限速。
/// 底层流由 HttpClient 拥有，因此默认不随本包装释放（leaveOpen）。
/// </summary>
internal sealed class ProgressWriteStream : DelegatingStream
{
    private readonly TrafficMeter _meter;

    public ProgressWriteStream(Stream inner, long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
        : base(inner)
    {
        _meter = new TrafficMeter(total, callback, maxRate);
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

    /// <summary>底层流由 HttpClient 拥有，不随本包装释放。</summary>
    protected override bool DisposeInnerStream => false;

    /// <summary>所有内容写完时调用，保证最后一次回调一定报告（loaded == total）。</summary>
    public void Complete() => _meter.Complete(upload: true);

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

        _meter.Report(force: false, upload: true);
    }
}
