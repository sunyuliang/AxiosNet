namespace AxiosNet.Internal.Headers;

/// <summary>字符区间的 ASCII 大小写不敏感比较与查找；不产生子串分配。</summary>
/// <remarks>只折叠 ASCII 字母大小写，非 ASCII 字符按原值比较。</remarks>
internal static class SpanComparisons
{
    /// <summary>两段字符区间是否忽略 ASCII 大小写后相等（长度必须相同）。</summary>
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

    /// <summary>与字符串忽略 ASCII 大小写比较；<paramref name="expected"/> 为 <c>null</c> 时仅当区间为空才相等。</summary>
    internal static bool EqualsIgnoreCase(ReadOnlySpan<char> value, string? expected)
        => expected is null ? value.IsEmpty : EqualsIgnoreCase(value, expected.AsSpan());

    /// <summary>是否以给定前缀开头（忽略 ASCII 大小写；空前缀恒为 <c>true</c>）。</summary>
    internal static bool StartsWithIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> prefix)
        => value.Length >= prefix.Length && EqualsIgnoreCase(value[..prefix.Length], prefix);

    /// <summary>是否以给定字符串前缀开头（忽略 ASCII 大小写）。</summary>
    internal static bool StartsWithIgnoreCase(ReadOnlySpan<char> value, string prefix)
        => StartsWithIgnoreCase(value, prefix.AsSpan());

    /// <summary>是否以给定后缀结尾（忽略 ASCII 大小写；空后缀恒为 <c>true</c>）。</summary>
    internal static bool EndsWithIgnoreCase(ReadOnlySpan<char> value, ReadOnlySpan<char> suffix)
        => value.Length >= suffix.Length && EqualsIgnoreCase(value[^suffix.Length..], suffix);

    /// <summary>是否以给定字符串后缀结尾（忽略 ASCII 大小写）。</summary>
    internal static bool EndsWithIgnoreCase(ReadOnlySpan<char> value, string suffix)
        => EndsWithIgnoreCase(value, suffix.AsSpan());

    /// <summary>是否包含给定片段（忽略 ASCII 大小写；空片段恒为 <c>true</c>）。</summary>
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

    /// <summary>是否包含给定字符串片段（忽略 ASCII 大小写）。</summary>
    internal static bool ContainsIgnoreCase(ReadOnlySpan<char> value, string needle)
        => ContainsIgnoreCase(value, needle.AsSpan());

    /// <summary>字符串是否包含给定片段（忽略 ASCII 大小写）。</summary>
    internal static bool ContainsIgnoreCase(string value, string needle)
        => ContainsIgnoreCase(value.AsSpan(), needle.AsSpan());

    /// <summary>ASCII 大写转小写；其它字符原样返回。</summary>
    internal static char ToLowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
