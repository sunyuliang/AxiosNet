using System.Globalization;
using System.Net.Http.Headers;

namespace AxiosNet.Internal;

/// <summary>
/// 内容头 <c>Content-Length</c> 的原始值读取与解析：不触发 BCL 的类型化解析，也不分配。
/// </summary>
/// <remarks>
/// 只在「恰好一个取值、且是合法的非负十进制整数」时返回该值；无该头、多条取值、带符号、非数字、溢出都为 <c>null</c>，
/// 由调用方按「长度未知」处理。取值前后的空白按 BCL 解析口径裁掉。
/// </remarks>
internal static class ContentLengthHeader
{
    /// <summary>内容头上 <c>Content-Length</c> 的头名。</summary>
    private const string HeaderName = "Content-Length";

    /// <summary>
    /// 取 <c>Content-Length</c> 的原始值并解析成字节数：不构造类型化头、不复制字符串。
    /// </summary>
    /// <param name="headers">内容头；为 <c>null</c> 时返回 <c>null</c>。</param>
    /// <returns>声明的字节数，或 <c>null</c>（未声明 / 形态不合预期）。</returns>
    public static long? ReadRaw(HttpContentHeaders? headers)
    {
        if (headers is null)
        {
            return null;
        }

#if NETSTANDARD2_0
        // 兼容资产没有 NonValidated：TryGetValues 会为取值装箱成集合，但只在调用方真的取值时才走这里。
        if (!headers.TryGetValues(HeaderName, out var values))
        {
            return null;
        }
#else
        // NonValidated 直接枚举头里存的原始字符串：既不解析也不分配（HeaderStringValues 是 ref struct）。
        if (!headers.NonValidated.TryGetValues(HeaderName, out var values))
        {
            return null;
        }
#endif

        long? parsed = null;
        var count = 0;

        foreach (var value in values)
        {
            if (++count > 1)
            {
                // 多条取值（含被合并成「1, 1」的形态）：口径不唯一，按未知处理。
                return null;
            }

            parsed = Parse(value);
        }

        return count == 1 ? parsed : null;
    }

    /// <summary>十进制非负整数解析：拒绝正负号、分隔符与任何非数字字符。</summary>
    private static long? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

#if NETSTANDARD2_0
        // 兼容资产没有 span 版 TryParse：先裁空白再按字符串解析（只有真的带空白时才多一次分配）。
        var trimmed = value.Trim();
        return trimmed.Length > 0
               && long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var netStandardLength)
            ? netStandardLength
            : null;
#else
        var span = value.AsSpan().Trim();
        return span.Length > 0 && long.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
#endif
    }
}
