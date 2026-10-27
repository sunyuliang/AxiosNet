using System.Runtime.CompilerServices;

namespace AxiosNet.Internal;

/// <summary>
/// 把响应原始字节解码为文本。优先级：显式 <see cref="AxiosConfig.SerializationSettings.ResponseEncoding"/>
/// → Content-Type charset → BOM → UTF-8；未知 charset 不抛异常，回落到 BOM / UTF-8。
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

        // 剥离匹配的 BOM（与 .NET 一致）。
        var bomLength = GetPreambleLength(bytes, encoding);
        return encoding.GetString(bytes[bomLength..]);
    }

    /// <summary>
    /// 已解析编码的解码（流式分帧用，编码解析一次后逐行复用）；<paramref name="resolved"/> 为 <c>null</c> 时按 BOM → UTF-8 兜底。
    /// </summary>
    public static string DecodeResolved(ReadOnlySpan<byte> bytes, Encoding? resolved)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = resolved ?? DetectFromBom(bytes) ?? Encoding.UTF8;
        var bomLength = GetPreambleLength(bytes, encoding);
        return encoding.GetString(bytes[bomLength..]);
    }

    /// <summary>
    /// charset 能否用于按字节找 <c>0x0A</c> 的流式分帧：ASCII 兼容编码为 <c>true</c>，UTF-16 / UTF-32 为 <c>false</c>，空 / 未知 charset 为 <c>true</c>。
    /// </summary>
    public static bool IsAsciiCompatible(string? charSet) => IsAsciiCompatible(ResolveFromCharset(charSet));

    /// <summary>已解析编码的版本：<c>null</c> 表示未指定（按 UTF-8），返回 <c>true</c>。</summary>
    public static bool IsAsciiCompatible(Encoding? encoding)
    {
        if (encoding is null)
        {
            return true;
        }

        Span<byte> probe = stackalloc byte[16];
        var written = encoding.GetBytes("A\n", probe);
        return written == 2 && probe[0] == (byte)'A' && probe[1] == (byte)'\n';
    }

    internal static Encoding? ResolveFromCharset(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return null;
        }

        // 允许 charset="utf-8" 这种带引号的写法。
        var name = charSet.AsSpan().Trim().Trim('"');

        // 常见 charset 直接用内置实例，省掉名称表查找。
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
            // 未知 charset / 未注册的代码页：交由 BOM / 默认编码兜底。
            return null;
        }
    }

    internal static Encoding? DetectFromBom(ReadOnlySpan<byte> bytes)
    {
        // 先判 4 字节的 UTF-32 BOM，再判 2 字节的 UTF-16 BOM。
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

    // GetPreamble() 每次调用都新建数组，按编码实例缓存（键随编码一起被 GC 回收）。
    private static readonly ConditionalWeakTable<Encoding, byte[]> Preambles = new();

    private static ReadOnlySpan<byte> PreambleOf(Encoding encoding)
        => Preambles.GetValue(encoding, static e => e.GetPreamble());

    private static int GetPreambleLength(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var preamble = PreambleOf(encoding);
        if (preamble.Length > 0
            && bytes.Length >= preamble.Length
            && bytes[..preamble.Length].SequenceEqual(preamble))
        {
            return preamble.Length;
        }

        return 0;
    }
}
