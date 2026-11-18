using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 给「本次发送」用的一份头隔离外壳：正文仍由内层内容序列化（零额外缓冲），但头集合是独立副本。
/// <c>OnSendingRequest</c> 改写 <c>request.Content.Headers</c> 时只影响这一个外壳，
/// 不会污染模板正文（后续重试）或其它对冲分支共享的那一份。
/// </summary>
/// <remarks>
/// 本类不拥有内层正文：不覆写 <see cref="IDisposable.Dispose"/>，因此释放外壳不会释放构造时传入的内容。
/// 内层归创建它的模板所有（重试 / 换端点 / 对冲分支共享同一份）。
/// </remarks>
internal sealed class HeaderIsolatedContent : HttpContent
{
    private readonly HttpContent _inner;

    private readonly long? _length;

    /// <summary>用内层正文构造外壳：正文不复制，只把头集合拷成独立副本。</summary>
    /// <param name="inner">内容还是它的那份正文（重试 / 对冲分支共享的模板）。</param>
    public HeaderIsolatedContent(HttpContent inner)
    {
        _inner = inner;

        // 复制头；Content-Length 是计算属性，读一次让它定型（长度未知时为 null）。
        _length = ContentHeaderCopy.CopyFrom(inner, this);
    }

    /// <summary>正文原样转发给内层内容序列化（零额外缓冲）；本类只隔开请求头。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="context">传输上下文（原样忽略）。</param>
    /// <returns>写出完成的任务。</returns>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => _inner.CopyToAsync(stream);

#if !NETSTANDARD2_0
    /// <summary>带令牌的写出：netstandard2.0 的 HttpContent 没有该虚重载，那里只保留无令牌版本。</summary>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => _inner.CopyToAsync(stream, context, cancellationToken);
#endif

    /// <summary>长度沿用构造时从内层复制定型的 <c>Content-Length</c>：未知时返回 <c>false</c>，交给分块传输。</summary>
    /// <param name="length">已知时为长度，否则为 <c>0</c>。</param>
    /// <returns>长度是否已知。</returns>
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
