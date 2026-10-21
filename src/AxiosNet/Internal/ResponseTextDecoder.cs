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
        return encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
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

    private static Encoding? DetectFromBom(byte[] bytes)
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

    private static int GetPreambleLength(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0
            && bytes.Length >= preamble.Length
            && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble))
        {
            return preamble.Length;
        }

        return 0;
    }
}
