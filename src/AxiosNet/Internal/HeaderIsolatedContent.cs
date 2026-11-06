using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 给「本次发送」用的一份头隔离外壳：正文仍由内层内容序列化（零额外缓冲），但头集合是独立副本。
/// <c>OnSendingRequest</c> 改写 <c>request.Content.Headers</c> 时只影响这一个外壳，
/// 不会污染模板正文（后续重试）或其它对冲分支共享的那一份。
/// </summary>
internal sealed class HeaderIsolatedContent : HttpContent
{
    private readonly HttpContent _inner;

    private readonly long? _length;

    public HeaderIsolatedContent(HttpContent inner)
    {
        _inner = inner;

        // 复制头；Content-Length 是计算属性，读一次让它定型（长度未知时为 null）。
        _length = ContentHeaderCopy.CopyFrom(inner, this);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _inner.CopyToAsync(stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => _inner.CopyToAsync(stream, context, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        if (_length is { } known)
        {
            length = known;
            return true;
        }

        length = 0;
        return false;
    }
}
