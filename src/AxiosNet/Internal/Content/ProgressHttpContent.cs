using System.Net;

namespace AxiosNet.Internal.Content;

/// <summary>
/// 上传侧包装：复用内部 <see cref="HttpContent"/> 的序列化逻辑，报告发送进度并按速率限流。
/// </summary>
internal sealed class ProgressHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly AxiosConfig _config;

    /// <summary>计量器挂在内容上复用：重发同一份正文时进度单调推进。</summary>
    private readonly TrafficMeter _meter;

    public ProgressHttpContent(HttpContent inner, Action<AxiosProgressEvent>? callback, long? maxRate, AxiosConfig config)
    {
        _inner = inner;
        _config = config;

        _meter = new TrafficMeter(ContentHeaderCopy.CopyFrom(inner, this), callback, maxRate);
    }

    // netstandard2.0 只有无令牌重载，写出逻辑抽成共用核心。
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => WriteWithProgressAsync(stream, CancellationToken.None);

#if !NETSTANDARD2_0
    /// <summary>写正文：内部内容的字节流经计量包装写出，边写边回调进度，结束后补发最后一帧。</summary>
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
        => WriteWithProgressAsync(stream, cancellationToken);
#endif

    /// <summary>带进度计量地写出一轮正文。</summary>
    /// <remarks>netstandard2.0 的 HttpContent 不提供带令牌的虚重载，那里固定传 <see cref="CancellationToken.None"/>。</remarks>
    private async Task WriteWithProgressAsync(Stream stream, CancellationToken cancellationToken)
    {
        _meter.Restart();

        var wrapper = new ProgressWriteStream(stream, _meter, MapCallbackError);

        // 写正文的 IO / 传输异常原样冒泡（ERR_NETWORK）。
        await _inner.CopyToAsync(wrapper, cancellationToken).ConfigureAwait(false);
        await wrapper.FlushAsync(cancellationToken).ConfigureAwait(false);

        Complete();
    }

    /// <summary>把进度回调异常翻译成 <c>ERR_BAD_REQUEST</c>（原始异常进 Cause）。</summary>
    private Exception MapCallbackError(Exception ex)
        => AxiosException.CallbackFailed(_config, null, nameof(AxiosConfig.ObservabilitySettings.OnUploadProgress), ex, upload: true);

    /// <summary>收尾补发进度回调。</summary>
    private void Complete()
    {
        try
        {
            _meter.Complete(upload: true);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw MapCallbackError(ex);
        }
    }

    /// <summary>长度沿用内部内容声明的 <c>Content-Length</c>；未知时返回 false 走分块传输。</summary>
    protected override bool TryComputeLength(out long length)
    {
        var declared = _inner.Headers.ContentLength;
        if (declared is long value)
        {
            length = value;
            return true;
        }

        length = 0;
        return false;
    }

    /// <summary>释放内部内容（本包装不持有其它资源）。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
