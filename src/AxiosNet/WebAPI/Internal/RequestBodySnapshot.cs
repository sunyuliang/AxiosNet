using System.Net.Http.Headers;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 401 重放用的正文快照：在首次发送失败（401）之后把内存缓冲的 HttpContent 读成字节并记下 <c>Content-Type</c>，
/// 重放时用快照新建内容实例。
/// </summary>
/// <remarks>
/// 不能在首次发送前读：没有 401 的默认路径会白付一份正文大小（<c>ReadAsByteArrayAsync</c> 还会多一倍缓冲）。
/// 401 时发送管线会保留模板正文（见 <c>AxiosConfig.RetainBodyContent</c>），快照读的是保留下来、尚未释放的那份。
/// 流与含流部件的 multipart 不在此列（库不做二次读取，也不截断）。
/// </remarks>
internal sealed class RequestBodySnapshot
{
    private readonly byte[] _bytes;
    private readonly string? _contentType;

    private RequestBodySnapshot(byte[] bytes, string? contentType)
    {
        _bytes = bytes;
        _contentType = contentType;
    }

    /// <summary>用快照字节新建一份可直接发送的正文。</summary>
    public HttpContent CreateContent()
    {
        var content = new ByteArrayContent(_bytes);
        if (_contentType is not null)
        {
            // 每次重建时重新解析：头值实例不跨内容共享，boundary / charset 等参数原样保留。
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
        }

        return content;
    }

    /// <summary>读一份快照；超过 <paramref name="maxBytes"/> 时返回 <c>null</c>（不截断正文，调用方据此退成不可重放）。</summary>
    /// <param name="content">内存缓冲的白名单内容（调用方已按内核分类器确认）。</param>
    /// <param name="maxBytes">请求体上限；<c>null</c> 表示不限制。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    public static async ValueTask<RequestBodySnapshot?> TryCreateAsync(
        HttpContent content,
        long? maxBytes,
        CancellationToken cancellationToken)
    {
        // 已知长度先判一次，避免为必然超限的正文分配缓冲。
        if (maxBytes is long limit && content.Headers.ContentLength is long known && known > limit)
        {
            return null;
        }

        // 用 ReadAsByteArrayAsync 读取：内容自带缓冲，读一次后首次发送复用同一份缓冲，不产生第二次序列化。
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (maxBytes is long limitAfterRead && bytes.Length > limitAfterRead)
        {
            return null;
        }

        return new RequestBodySnapshot(bytes, content.Headers.ContentType?.ToString());
    }
}
