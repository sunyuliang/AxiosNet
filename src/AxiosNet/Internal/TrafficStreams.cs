using System.Diagnostics;

namespace AxiosNet.Internal;

/// <summary>
/// 上传/下载共用的流量计量与进度上报：
/// 累计字节、按 50ms 节流回调、按 <see cref="AxiosConfig.LimitSettings.MaxRate"/> 反算应等待的时间。
/// </summary>
internal sealed class TrafficMeter
{
    private const int ReportIntervalMs = 50;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long? _total;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;

    private long _loaded;

    /// <summary>
    /// 本轮实际传输的起点（累计值的快照）。重发正文时 <see cref="Restart"/> 把它推到当前累计值，
    /// 「已传满声明长度」的判定按<b>本轮</b>增量算 —— 否则第二轮一开始
    /// <c>_loaded &gt;= _total</c> 就恒真，第一次上报就把计量器锁死，终值远小于真实发送量。
    /// </summary>
    private long _roundStart;

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

        // 已经传满<b>本轮</b>的声明长度：后续 EOF / Complete 不再重复报一次 100%。
        // 必须要求 total > 0：total 为 0（服务端把 Content-Length 写成 0 却仍有正文，
        // 或注入的 Handler 不按声明长度截断）时，0 >= 0 恒真 —— 第一次上报就会把计量器锁死，
        // 之后几百 KB 全部静默，调用方只看到一次「loaded=第一块 / total=0」。
        if (_total is long total && total > 0 && _loaded - _roundStart >= total)
        {
            _completed = true;
        }
    }

    /// <summary>完成时强制上报一次（loaded == total）。</summary>
    public void Complete(bool upload) => Report(force: true, upload);

    /// <summary>
    /// 开始新一次实际传输（重发正文时调用）：只解除「已完成」状态，<b>累计值与计时器保留</b> ——
    /// 307/308 重定向或重试会再次写出同一份正文，共用计量器才能让进度单调推进而不是掉回 0。
    /// <para>同时把「本轮起点」推到当前累计值：完成判定按增量算，第二轮才不会一开始就判成已传满。</para>
    /// </summary>
    public void Restart()
    {
        _completed = false;
        _roundStart = _loaded;
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
}

/// <summary>
/// 下载侧流量包装：统计已读字节、按 <see cref="AxiosConfig.CallbackSettings.OnDownloadProgress"/> 回报进度、
/// 按 <see cref="AxiosConfig.LimitSettings.MaxRate"/> 限速（读满配额后按速率等待），
/// 并在累计字节超出 <see cref="AxiosConfig.LimitSettings.MaxContentLength"/> 时立刻中断。
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

    /// <summary>
    /// 回报进度：回调是请求管线的一部分，抛出的异常不能以裸类型交给调用方 ——
    /// 统一成 <c>ERR_BAD_RESPONSE</c>（正文已开始接收），原始异常放在 Cause 里。
    /// </summary>
    private void Report(bool force)
    {
        try
        {
            _meter.Report(force, upload: false);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.ProgressCallbackFailed(
                _config,
                _request,
                nameof(AxiosConfig.CallbackSettings.OnDownloadProgress),
                ex,
                upload: false);
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

    /// <summary>
    /// 由外部提供计量器：同一个 <see cref="HttpContent"/> 可能被序列化多次（重定向 / 重试重发正文），
    /// 计量器必须挂在内容上共享，不能每次为包装流新建一个。
    /// </summary>
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

    /// <summary>底层流由 HttpClient 拥有，不随本包装释放。</summary>
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
            // 回调异常必须换成统一错误模型：否则 HttpClient 会把它包成 HttpRequestException，
            // 上层再翻译成 ERR_NETWORK —— 一次「回调写错」被误报成「网络故障」。
            throw _mapCallbackError(ex);
        }
    }
}
