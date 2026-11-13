namespace AxiosNet.Internal;

/// <summary>
/// charset 名 → <see cref="Encoding"/> 的唯一解析：请求侧（写正文编码）与响应侧（解码正文）共用同一张内置表与同一套失败口径，
/// 避免一侧认 <c>utf-8</c>、另一侧认不出来。
/// </summary>
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

        // 常见 charset 直接用内置实例。
        if (trimmed.Equals(Utf8Name, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        if (trimmed.Equals(AsciiName, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.ASCII;
        }

        if (trimmed.Equals(Latin1Name, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformCompat.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(trimmed.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 未知 charset / 未注册的代码页：交给调用点兜底。
            return null;
        }
    }
}
