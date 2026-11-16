namespace AxiosNet.Internal;

/// <summary>
/// 字符区间的 ASCII 大小写不敏感比较与查找：逐字符手写，三个目标框架共用一份实现，不产生子串分配。
/// </summary>
/// <remarks>
/// 只处理 ASCII 字母的大小写折叠；非 ASCII 字符按原值比较，因此不会与 <see cref="StringComparison.OrdinalIgnoreCase"/>
/// 在非 ASCII 上取得相同结果。调用点比较的都是 HTTP 头名、媒体类型、charset 名这类协议 token。
/// </remarks>
internal static class SpanComparisons
{
    /// <summary>两段字符区间是否忽略 ASCII 大小写后相等（长度必须相同）。</summary>
    /// <param name="value">待比较的字符区间。</param>
    /// <param name="expected">期望值。</param>
    /// <returns>长度相同且逐字符折叠后相等时为 <c>true</c>。</returns>
    internal static bool EqualsIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (ToLowerAscii(value[i]) != ToLowerAscii(expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>字符区间与字符串是否忽略 ASCII 大小写后相等。</summary>
    /// <param name="value">待比较的字符区间。</param>
    /// <param name="expected">期望值；为 <c>null</c> 时仅当区间为空才相等。</param>
    /// <returns>相等时为 <c>true</c>。</returns>
    internal static bool EqualsIgnoreCase(ReadOnlySpan<char> value, string? expected)
        => expected is null ? value.IsEmpty : EqualsIgnoreCase(value, expected.AsSpan());

    /// <summary>字符区间是否以给定前缀开头（忽略 ASCII 大小写）。</summary>
    /// <param name="value">待检查的字符区间。</param>
    /// <param name="prefix">前缀；空串恒为 <c>true</c>。</param>
    /// <returns>以该前缀开头时为 <c>true</c>。</returns>
    internal static bool StartsWithIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> prefix)
        => value.Length >= prefix.Length && EqualsIgnoreCase(value[..prefix.Length], prefix);

    /// <summary>字符区间是否以给定前缀开头（忽略 ASCII 大小写）。</summary>
    /// <param name="value">待检查的字符区间。</param>
    /// <param name="prefix">前缀字符串；空串恒为 <c>true</c>。</param>
    /// <returns>以该前缀开头时为 <c>true</c>。</returns>
    internal static bool StartsWithIgnoreCase(ReadOnlySpan<char> value, string prefix)
        => StartsWithIgnoreCase(value, prefix.AsSpan());

    /// <summary>字符区间是否以给定后缀结尾（忽略 ASCII 大小写）。</summary>
    /// <param name="value">待检查的字符区间。</param>
    /// <param name="suffix">后缀；空串恒为 <c>true</c>。</param>
    /// <returns>以该后缀结尾时为 <c>true</c>。</returns>
    internal static bool EndsWithIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> suffix)
        => value.Length >= suffix.Length && EqualsIgnoreCase(value[^suffix.Length..], suffix);

    /// <summary>字符区间是否以给定后缀结尾（忽略 ASCII 大小写）。</summary>
    /// <param name="value">待检查的字符区间。</param>
    /// <param name="suffix">后缀字符串；空串恒为 <c>true</c>。</param>
    /// <returns>以该后缀结尾时为 <c>true</c>。</returns>
    internal static bool EndsWithIgnoreCase(ReadOnlySpan<char> value, string suffix)
        => EndsWithIgnoreCase(value, suffix.AsSpan());

    /// <summary>字符区间是否包含给定片段（忽略 ASCII 大小写，取首个匹配位置）。</summary>
    /// <param name="value">待查找的字符区间。</param>
    /// <param name="needle">要查找的片段；为空时恒为 <c>true</c>。</param>
    /// <returns>包含时为 <c>true</c>。</returns>
    internal static bool ContainsIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> needle)
    {
        if (needle.IsEmpty)
        {
            return true;
        }

        for (var i = 0; i <= value.Length - needle.Length; i++)
        {
            if (EqualsIgnoreCase(value.Slice(i, needle.Length), needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>字符区间是否包含给定片段（忽略 ASCII 大小写，取首个匹配位置）。</summary>
    /// <param name="value">待查找的字符区间。</param>
    /// <param name="needle">要查找的片段字符串；为空时恒为 <c>true</c>。</param>
    /// <returns>包含时为 <c>true</c>。</returns>
    internal static bool ContainsIgnoreCase(ReadOnlySpan<char> value, string needle)
        => ContainsIgnoreCase(value, needle.AsSpan());

    /// <summary>字符串是否包含给定片段（忽略 ASCII 大小写，取首个匹配位置）。</summary>
    /// <param name="value">待查找的字符串。</param>
    /// <param name="needle">要查找的片段；为空时恒为 <c>true</c>。</param>
    /// <returns>包含时为 <c>true</c>。</returns>
    internal static bool ContainsIgnoreCase(string value, string needle)
        => ContainsIgnoreCase(value.AsSpan(), needle.AsSpan());

    /// <summary>ASCII 大写转小写；其它字符原样返回。</summary>
    /// <param name="c">待转换字符。</param>
    /// <returns>小写形式。</returns>
    internal static char ToLowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
