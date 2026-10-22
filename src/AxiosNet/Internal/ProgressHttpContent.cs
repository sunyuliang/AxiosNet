using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 上传侧包装：复用内部 <see cref="HttpContent"/> 的序列化逻辑，同时报告发送进度并按速率限流。
/// 头部从内部内容复制，因此外层表现与原内容一致。
/// </summary>
internal sealed class ProgressHttpContent : HttpContent
{
    private readonly HttpContent _inner;

    /// <summary>
    /// 计量器挂在内容上而不是每次序列化时新建：307/308 重定向或重试会再次调用
    /// <see cref="SerializeToStreamAsync(Stream, TransportContext?, CancellationToken)"/> 重发同一份正文，
    /// 共用计量器才能让进度**单调推进**（重发字节继续累加），而不是跳回 0。
    /// </summary>
    private readonly TrafficMeter _meter;

    public ProgressHttpContent(HttpContent inner, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _inner = inner;

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

        var wrapper = new ProgressWriteStream(stream, _meter);

        await _inner.CopyToAsync(wrapper, cancellationToken).ConfigureAwait(false);
        await wrapper.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 保证这一轮结束时最后一次回调一定到达
        _meter.Complete(upload: true);
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
