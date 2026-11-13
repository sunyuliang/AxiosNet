using System;

namespace AxiosNet;

/// <summary>
/// URL 构造专用的字符区间拼接入口：net6+ 直接转发 BCL 的 span 重载（只分配结果字符串），
/// netstandard2.0 没有这些重载，退化成一次 char[] 拷贝再构串。
/// 集中在这一处，是为了让 <c>UrlBuilder</c> 的拼接语义在三份资产上完全一致，
/// 同时避免在业务源码里对每个片段写 .ToString()（那会按片段数多分配若干中间字符串）。
/// </summary>
internal static class SpanStringConcat
{
    /// <summary>拼接两段字符区间。</summary>
    /// <param name="first">第一段。</param>
    /// <param name="second">第二段。</param>
    /// <returns>拼接后的字符串。</returns>
    internal static string Concat(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
        => Concat(first, second, default, default);

    /// <summary>拼接三段字符区间。</summary>
    /// <param name="first">第一段。</param>
    /// <param name="second">第二段。</param>
    /// <param name="third">第三段。</param>
    /// <returns>拼接后的字符串。</returns>
    internal static string Concat(ReadOnlySpan<char> first, ReadOnlySpan<char> second, ReadOnlySpan<char> third)
        => Concat(first, second, third, default);

    /// <summary>拼接四段字符区间（2/3 段重载最终都落到这里，兼容分支只存在一份）。</summary>
    /// <param name="first">第一段。</param>
    /// <param name="second">第二段。</param>
    /// <param name="third">第三段。</param>
    /// <param name="fourth">第四段。</param>
    /// <returns>拼接后的字符串。</returns>
    internal static string Concat(
        ReadOnlySpan<char> first,
        ReadOnlySpan<char> second,
        ReadOnlySpan<char> third,
        ReadOnlySpan<char> fourth)
    {
#if NETSTANDARD2_0
        var buffer = new char[first.Length + second.Length + third.Length + fourth.Length];
        var offset = 0;
        CopyInto(buffer, ref offset, first);
        CopyInto(buffer, ref offset, second);
        CopyInto(buffer, ref offset, third);
        CopyInto(buffer, ref offset, fourth);
        return new string(buffer);
#else
        return string.Concat(first, second, third, fourth);
#endif
    }

#if NETSTANDARD2_0
    /// <summary>把一段字符区间拷进结果缓冲区并推进写入位置；兼容资产专用的拼接内循环。</summary>
    /// <param name="buffer">结果缓冲区。</param>
    /// <param name="offset">当前写入位置，调用后加上本段长度。</param>
    /// <param name="part">待拷入的字符区间。</param>
    private static void CopyInto(char[] buffer, ref int offset, ReadOnlySpan<char> part)
    {
        // AsSpan(int) 由 System.Memory 提供；一次拷贝覆盖整段，不做逐字符循环。
        part.CopyTo(buffer.AsSpan(offset));
        offset += part.Length;
    }
#endif
}
