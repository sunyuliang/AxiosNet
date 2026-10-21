using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 上传侧包装：复用内部 <see cref="HttpContent"/> 的序列化逻辑，同时报告发送进度并按速率限流。
/// 头部从内部内容复制，因此外层表现与原内容一致。
/// </summary>
internal sealed class ProgressHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Action<AxiosProgressEvent>? _callback;
    private readonly long? _maxRate;

    public ProgressHttpContent(HttpContent inner, Action<AxiosProgressEvent>? callback, long? maxRate)
    {
        _inner = inner;
        _callback = callback;
        _maxRate = maxRate;

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
        // 内容长度已知时进度才有百分比；未知时只有 loaded。
        var total = _inner.Headers.ContentLength;
        var wrapper = new ProgressWriteStream(stream, total, _callback, _maxRate);

        await _inner.CopyToAsync(wrapper, cancellationToken).ConfigureAwait(false);
        await wrapper.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 保证最后一次回调一定到达（loaded == total）
        wrapper.Complete();
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
