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
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
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
    {
        _inner = inner;
        _meter = new TrafficMeter(total, callback, maxRate);
        _contentLimit = contentLimit;
        _config = config;
        _request = request;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>
    /// 同步读：阻塞等待异步核心（限速等待也走 Task.Delay 的异步路径），不把同步 I/O 压给内层流。
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // ReadByte 不覆写：基类实现会换算成 Read(byte[], int, int)，落到同一路径（进度/限速语义一致）。

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadCoreAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// 上传侧流量包装：写入底层传输流时统计字节、回报进度并限速。
/// 底层流由 HttpClient 拥有，因此默认不随本包装释放（leaveOpen）。
/// </summary>
internal sealed class ProgressWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly TrafficMeter _meter;

    public ProgressWriteStream(Stream inner, long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _inner = inner;
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

    /// <summary>
    /// 同步写：与同步读同理，阻塞等待异步核心（限速等待走 Task.Delay 的异步路径）。
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
        => BlockingWait.Wait(WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None));

    // WriteByte 不覆写：基类实现会换算成 Write(byte[], int, int)，落到同一路径。

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WriteCoreAsync(buffer, cancellationToken);

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TrackAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所有内容写完时调用，保证最后一次回调一定报告（loaded == total）。</summary>
    public void Complete() => _meter.Complete(upload: true);

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

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
