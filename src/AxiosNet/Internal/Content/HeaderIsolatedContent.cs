using System.Net;

namespace AxiosNet.Internal.Content;

/// <summary>给「本次发送」用的一份头隔离外壳：正文仍由内层内容序列化，头集合是独立副本。</summary>
/// <remarks><c>OnSendingRequest</c> 改写 <c>request.Content.Headers</c> 只影响这一个外壳；本类不拥有内层正文，释放外壳不释放内层。</remarks>
internal sealed class HeaderIsolatedContent : HttpContent
{
    private readonly HttpContent _inner;

    private readonly long? _length;

    /// <summary>用内层正文构造外壳：正文不复制，只把头集合拷成独立副本。</summary>
    public HeaderIsolatedContent(HttpContent inner)
    {
        _inner = inner;

        // Content-Length 是计算属性，读一次让它定型。
        _length = ContentHeaderCopy.CopyFrom(inner, this);
    }

    /// <summary>正文原样转发给内层内容序列化；本类只隔开请求头。</summary>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _inner.CopyToAsync(stream);

#if !NETSTANDARD2_0
    /// <summary>带令牌的写出：netstandard2.0 的 HttpContent 没有该虚重载，那里只保留无令牌版本。</summary>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => _inner.CopyToAsync(stream, context, cancellationToken);
#endif

    /// <summary>长度沿用构造时复制的 <c>Content-Length</c>；未知时返回 <c>false</c>，交给分块传输。</summary>
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
