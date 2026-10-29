using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 上传侧包装：复用内部 <see cref="HttpContent"/> 的序列化逻辑，报告发送进度并按速率限流；头部从内部内容复制。
/// </summary>
internal sealed class ProgressHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly AxiosConfig _config;

    /// <summary>
    /// 计量器挂在内容上复用：307/308 重定向与重试会重发同一份正文，共用计量器进度才单调推进。
    /// </summary>
    private readonly TrafficMeter _meter;

    public ProgressHttpContent(HttpContent inner, Action<AxiosProgressEvent>? callback, long? maxRate, AxiosConfig config)
    {
        _inner = inner;
        _config = config;

        // 内容长度已知时进度才有百分比；未知时只有 loaded。
        _meter = new TrafficMeter(inner.Headers.ContentLength, callback, maxRate);

        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    // net6.0 上无令牌的 SerializeToStreamAsync(Stream, TransportContext) 仍是抽象成员，必须实现。
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        // 新的一轮写出：累计值与计时器保留，只解除「已完成」状态
        _meter.Restart();

        var wrapper = new ProgressWriteStream(stream, _meter, MapCallbackError);

        // 不能把整段 CopyToAsync 的异常都翻译成「进度回调失败」：写正文的 IOException /
        // HttpRequestException 是网络故障，必须原样冒泡到 AxiosClient 的错误模型（ERR_NETWORK）。
        await _inner.CopyToAsync(wrapper, cancellationToken).ConfigureAwait(false);
        await wrapper.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 保证这一轮结束时最后一次回调一定到达
        Complete();
    }

    /// <summary>把进度回调的任意异常翻译成统一错误模型（<c>ERR_BAD_REQUEST</c>，原始异常进 Cause）。</summary>
    private Exception MapCallbackError(Exception ex)
        => AxiosException.CallbackFailed(_config, null, nameof(AxiosConfig.CallbackSettings.OnUploadProgress), ex, upload: true);

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
