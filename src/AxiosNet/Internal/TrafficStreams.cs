using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 下载侧流量包装：统计已读字节、按 <see cref="AxiosConfig.OnDownloadProgress"/> 回报进度，
/// 并按 <see cref="AxiosConfig.MaxRate"/> 限速（读满配额后按速率等待）。
/// </summary>
internal sealed class ProgressReadStream : Stream
{
    private const int ReportIntervalMs = 50;

    private readonly Stream _inner;
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _loaded;
    private long _lastReportMs = -ReportIntervalMs;
    private bool _completed;

    public ProgressReadStream(Stream inner, long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _inner = inner;
        _total = total;
        _callback = callback;
        _maxRate = maxRate;
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Track(read);
        return read;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        Track(value < 0 ? 0 : 1);
        return value;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        await TrackAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TrackAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    private void Track(int read)
    {
        if (read <= 0)
        {
            Report(force: true);
            return;
        }

        _loaded += read;
        ThrottleSync();
        Report(force: false);
    }

    private async ValueTask TrackAsync(int read, CancellationToken cancellationToken)
    {
        if (read <= 0)
        {
            Report(force: true);
            return;
        }

        _loaded += read;
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);
        Report(force: false);
    }

    private void ThrottleSync()
    {
        var delay = ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private async ValueTask ThrottleAsync(CancellationToken cancellationToken)
    {
        var delay = ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan ComputeDelay()
    {
        if (_maxRate is not long rate || rate <= 0)
        {
            return TimeSpan.Zero;
        }

        var expected = TimeSpan.FromSeconds((double)_loaded / rate);
        var delta = expected - _clock.Elapsed;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }

    private void Report(bool force)
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
        _callback(BuildEvent(upload: false));

        // 已经读满声明长度：后续 EOF 不再重复报一次 100%
        if (_total is long total && _loaded >= total)
        {
            _completed = true;
        }
    }

    private AxiosProgressEvent BuildEvent(bool upload)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        double? rate = seconds > 0 && _loaded > 0 ? _loaded / seconds : null;
        TimeSpan? estimated = rate is > 0 && _total is long total && total > _loaded
            ? TimeSpan.FromSeconds((total - _loaded) / rate.Value)
            : null;

        return new AxiosProgressEvent(_loaded, _total, rate, estimated, upload);
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
    private const int ReportIntervalMs = 50;

    private readonly Stream _inner;
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _loaded;
    private long _lastReportMs = -ReportIntervalMs;
    private bool _completed;

    public ProgressWriteStream(Stream inner, long? total, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _inner = inner;
        _total = total;
        _callback = callback;
        _maxRate = maxRate;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _loaded;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Track(count);
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        Track(1);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        await TrackAsync(count, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await TrackAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所有内容写完时调用，保证最后一次回调一定报告（loaded == total）。</summary>
    public void Complete() => Report(force: true);

    private void Track(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _loaded += count;
        var delay = ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }

        Report(force: false);
    }

    private async ValueTask TrackAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        _loaded += count;
        var delay = ComputeDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        Report(force: false);
    }

    private TimeSpan ComputeDelay()
    {
        if (_maxRate is not long rate || rate <= 0)
        {
            return TimeSpan.Zero;
        }

        var expected = TimeSpan.FromSeconds((double)_loaded / rate);
        var delta = expected - _clock.Elapsed;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }

    private void Report(bool force)
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

        var seconds = _clock.Elapsed.TotalSeconds;
        double? rate = seconds > 0 && _loaded > 0 ? _loaded / seconds : null;
        TimeSpan? estimated = rate is > 0 && _total is long total && total > _loaded
            ? TimeSpan.FromSeconds((total - _loaded) / rate.Value)
            : null;

        _callback(new AxiosProgressEvent(_loaded, _total, rate, estimated, upload: true));

        // 已经写满声明长度：Complete() 不再重复报一次 100%
        if (_total is { } declared && _loaded >= declared)
        {
            _completed = true;
        }
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
