using System.Net.Http.Headers;

namespace AxiosNet.Internal.Headers;

/// <summary>响应 <c>Content-Type</c> 的原始值读取与拆分：媒体类型与字符集按需解析，不构造 <see cref="MediaTypeHeaderValue"/>。</summary>
/// <remarks>拆分结果与 <see cref="MediaTypeHeaderValue.TryParse"/> 逐字一致；只有简单形态走快路径，其余回落 BCL。</remarks>
internal static class ContentTypeHeader
{
    /// <summary>响应内容头上 <c>Content-Type</c> 的头名。</summary>
    private const string HeaderName = "Content-Type";

    /// <summary>字符集参数名（比较不区分大小写）。</summary>
    private const string CharSetParameter = "charset";

    /// <summary>取 <c>Content-Type</c> 的原始值（不类型化解析、不复制字符串）；没有该头时返回 <c>null</c>。</summary>
    public static string? ReadRaw(HttpContentHeaders? headers)
    {
        if (headers is null)
        {
            return null;
        }

#if NETSTANDARD2_0
        // netstandard2.0 没有 NonValidated，只能走 TryGetValues。
        return headers.TryGetValues(HeaderName, out var values) ? values.FirstOrDefault() : null;
#else
        // NonValidated 直接枚举头里的原始字符串，不解析也不分配。
        if (!headers.NonValidated.TryGetValues(HeaderName, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            return value;
        }

        return null;
#endif
    }

    /// <summary>把原始头值拆成媒体类型主值与字符集，两者都可能是 <c>null</c>。</summary>
    public static (string? MediaType, string? CharSet) Split(string? raw)
        => string.IsNullOrEmpty(raw)
            ? (null, null)
            : TrySplitSimple(raw.AsSpan(), wantMediaType: true, wantCharSet: true, out var mediaType, out var charSet)
                ? (mediaType, charSet)
                : ParseWithBcl(raw);

    /// <summary>只取媒体类型主值。</summary>
    public static string? MediaTypeOf(string? raw)
        => string.IsNullOrEmpty(raw)
            ? null
            : TrySplitSimple(raw.AsSpan(), wantMediaType: true, wantCharSet: false, out var mediaType, out _)
                ? mediaType
                : ParseWithBcl(raw).MediaType;

    /// <summary>只取字符集。</summary>
    public static string? CharSetOf(string? raw)
        => string.IsNullOrEmpty(raw)
            ? null
            : TrySplitSimple(raw.AsSpan(), wantMediaType: false, wantCharSet: true, out _, out var charSet)
                ? charSet
                : ParseWithBcl(raw).CharSet;

    /// <summary>权威解析：快路径不认识的形态交给 BCL。</summary>
    private static (string? MediaType, string? CharSet) ParseWithBcl(string raw)
        => MediaTypeHeaderValue.TryParse(raw, out var parsed) ? (parsed.MediaType, parsed.CharSet) : (null, null);

    /// <summary>简单形态的快路径：单趟扫描，任何不像 token 的形态返回 <c>false</c> 交给 BCL。</summary>
    private static bool TrySplitSimple(
        ReadOnlySpan<char> raw,
        bool wantMediaType,
        bool wantCharSet,
        out string? mediaType,
        out string? charSet)
    {
        mediaType = null;
        charSet = null;

        // ① 主值：token "/" token，允许其后有空白（"application/json ; charset=…" 是常见写法）。
        var slash = -1;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '/')
            {
                if (slash >= 0 || i == 0)
                {
                    return false;
                }

                slash = i;
                i++;
                continue;
            }

            if (c == ';' || c == ' ' || c == '\t')
            {
                break;
            }

            if (!HttpTokens.IsChar(c))
            {
                return false;
            }

            i++;
        }

        var mainEnd = i;
        if (slash <= 0 || slash >= mainEnd - 1)
        {
            // 没有 '/'、或某一半为空，交给 BCL 判。
            return false;
        }

        if (wantMediaType)
        {
            mediaType = raw[..mainEnd].ToString();
        }

        SkipWhitespace(raw, ref i);
        if (i >= raw.Length)
        {
            // 只有主值，没有参数。
            return true;
        }

        if (raw[i] != ';')
        {
            return false;
        }

        // ② 参数：每段都是 token "=" token，段间用 ';' 分隔。
        while (i < raw.Length)
        {
            // 此刻 raw[i] 一定是 ';'。
            i++;
            SkipWhitespace(raw, ref i);

            var nameStart = i;
            while (i < raw.Length && HttpTokens.IsChar(raw[i]))
            {
                i++;
            }

            if (i == nameStart)
            {
                // 空段 / 尾随 ';'：不能当成「没有参数」。
                return false;
            }

            var nameEnd = i;
            SkipWhitespace(raw, ref i);
            if (i >= raw.Length || raw[i] != '=')
            {
                return false;
            }

            i++;
            SkipWhitespace(raw, ref i);

            var valueStart = i;
            while (i < raw.Length && HttpTokens.IsChar(raw[i]))
            {
                i++;
            }

            if (i == valueStart)
            {
                // 空取值（"charset="）交给 BCL 定。
                return false;
            }

            var valueEnd = i;
            SkipWhitespace(raw, ref i);

            // 同名参数取第一个。
            if (wantCharSet
                && charSet is null
                && SpanComparisons.EqualsIgnoreCase(raw[nameStart..nameEnd], CharSetParameter))
            {
                charSet = raw[valueStart..valueEnd].ToString();
            }

            if (i >= raw.Length)
            {
                return true;
            }

            if (raw[i] != ';')
            {
                // 引号、转义、逗号等尾巴：不猜，交给 BCL。
                return false;
            }
        }

        return true;
    }

    /// <summary>跳过空格与水平制表符（RFC 7230 的 OWS）。</summary>
    private static void SkipWhitespace(ReadOnlySpan<char> raw, ref int index)
    {
        while (index < raw.Length && (raw[index] == ' ' || raw[index] == '\t'))
        {
            index++;
        }
    }
}
