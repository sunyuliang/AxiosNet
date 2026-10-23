namespace AxiosNet.Internal;

/// <summary>
/// 把响应原始字节解码为文本。优先级：显式 <see cref="AxiosConfig.ResponseEncoding"/> → Content-Type charset
/// → BOM → UTF-8，与 .NET 内置 HttpContent 一致，但可强制覆盖且未知 charset 不抛异常。
/// </summary>
internal static class ResponseTextDecoder
{
    private const string Utf8Name = "utf-8";
    private const string AsciiName = "us-ascii";
    private const string Latin1Name = "iso-8859-1";

    public static string Decode(byte[] bytes, string? charSet, Encoding? forced)
        => Decode(bytes.AsSpan(), charSet, forced);

    public static string Decode(ReadOnlySpan<byte> bytes, string? charSet, Encoding? forced)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = forced
            ?? ResolveFromCharset(charSet)
            ?? DetectFromBom(bytes)
            ?? Encoding.UTF8;

        // 即便已确定编码，也剥离匹配的 BOM（与 .NET 一致）。
        var bomLength = GetPreambleLength(bytes, encoding);
        return encoding.GetString(bytes[bomLength..]);
    }

    /// <summary>
    /// charset 能否用于「按字节找 <c>0x0A</c>」的流式分帧：ASCII 文本编码后必须仍是单字节，
    /// 且换行就是 <c>0x0A</c> 本身。UTF-16 / UTF-32 等宽编码返回 <c>false</c>。
    /// <para>空 charset（默认 UTF-8）与未知 charset（解码时回落默认编码）都返回 <c>true</c>。</para>
    /// </summary>
    public static bool IsAsciiCompatible(string? charSet)
    {
        var encoding = ResolveFromCharset(charSet);
        if (encoding is null)
        {
            return true;
        }

        Span<byte> probe = stackalloc byte[16];
        var written = encoding.GetBytes("A\n", probe);
        return written == 2 && probe[0] == (byte)'A' && probe[1] == (byte)'\n';
    }

    private static Encoding? ResolveFromCharset(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return null;
        }

        // 允许 charset="utf-8" 这种带引号的写法；切片判等不产生字符串。
        var name = charSet.AsSpan().Trim().Trim('"');

        // 最常见的几个 charset 直接用内置实例：GetEncoding 要先在名称表里做一次大小写不敏感查找。
        if (name.Equals(Utf8Name, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        if (name.Equals(AsciiName, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.ASCII;
        }

        if (name.Equals(Latin1Name, StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(name.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 未知 charset / 未注册的代码页（如未注册 CodePagesEncodingProvider 的 gbk）：交由 BOM/默认编码兜底。
            return null;
        }
    }

    private static Encoding? DetectFromBom(ReadOnlySpan<byte> bytes)
    {
        // 注意先判 4 字节的 UTF-32 BOM，再判 2 字节的 UTF-16 BOM。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return null;
    }

    private static int GetPreambleLength(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0
            && bytes.Length >= preamble.Length
            && bytes[..preamble.Length].SequenceEqual(preamble))
        {
            return preamble.Length;
        }

        return 0;
    }
}
