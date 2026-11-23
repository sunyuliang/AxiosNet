namespace AxiosNet.Internal.Response;

/// <summary>charset 名 → <see cref="Encoding"/> 的唯一解析：请求侧与响应侧共用同一张内置表与同一套失败口径。</summary>
internal static class EncodingResolver
{
    private const string Utf8Name = "utf-8";
    private const string AsciiName = "us-ascii";
    private const string Latin1Name = "iso-8859-1";

    /// <summary>按 charset 名取编码；空白、未知或平台不支持时返回 <c>null</c>，由调用点决定兜底还是报错。</summary>
    public static Encoding? TryResolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // 允许 charset="utf-8" 这种带引号的写法。
        var trimmed = name.AsSpan().Trim().Trim('"');
        if (trimmed.IsEmpty)
        {
            return null;
        }

        if (SpanComparisons.EqualsIgnoreCase(trimmed, Utf8Name))
        {
            return Encoding.UTF8;
        }

        if (SpanComparisons.EqualsIgnoreCase(trimmed, AsciiName))
        {
            return Encoding.ASCII;
        }

        if (SpanComparisons.EqualsIgnoreCase(trimmed, Latin1Name))
        {
            return PlatformCompat.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(trimmed.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 未知 charset / 未注册代码页：交给调用点兜底。
            return null;
        }
    }
}
