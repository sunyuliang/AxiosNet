namespace AxiosNet.Internal;

/// <summary>
/// 内容外壳的公共装配：把内层内容的头复制到外壳，并回传内层声明的长度。
/// 两处外壳（<see cref="ProgressHttpContent"/> / <see cref="HeaderIsolatedContent"/>）语义不同的部分各自保留，只共用这一段。
/// </summary>
internal static class ContentHeaderCopy
{
    /// <summary>复制头到 <paramref name="shell"/>，返回内层内容声明的 <c>Content-Length</c>（未知为 <c>null</c>）。</summary>
    public static long? CopyFrom(HttpContent inner, HttpContent shell)
    {
        foreach (var header in inner.Headers)
        {
            shell.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return inner.Headers.ContentLength;
    }
}
