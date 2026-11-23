using System.Net.Http.Headers;

namespace AxiosNet.Internal.Headers;

/// <summary>
/// 响应 <c>Content-Type</c> 的原始值读取与拆分：媒体类型与字符集按需解析，不构造 <see cref="MediaTypeHeaderValue"/>。
/// </summary>
/// <remarks>
/// 拆分结果必须与 <see cref="MediaTypeHeaderValue.TryParse"/> 逐字一致（主值、字符集，以及解析不了时两者皆为 <c>null</c>）。
/// 只有简单形态（ASCII、无引号 / 转义 / 逗号、每段 <c>name=value</c>）走快路径，其余一律回落 BCL 解析。
/// </remarks>
internal static class ContentTypeHeader
{
    /// <summary>响应内容头上 <c>Content-Type</c> 的头名。</summary>
    private const string HeaderName = "Content-Type";

    /// <summary>字符集参数名（比较不区分大小写）。</summary>
    private const string CharSetParameter = "charset";

    /// <summary>
    /// 取 <c>Content-Type</c> 的原始值：不触发 BCL 的类型化解析，也不复制字符串（返回头里已有的实例）。
    /// 没有该头时返回 <c>null</c>。
    /// </summary>
    /// <param name="headers">响应内容头；调用方保证在响应对象存活期间不释放底层消息。</param>
    /// <returns>原始头值，或 <c>null</c>。</returns>
    public static string? ReadRaw(HttpContentHeaders? headers)
    {
        if (headers is null)
        {
            return null;
        }

#if NETSTANDARD2_0
        // 兼容资产没有 NonValidated：只能走 TryGetValues（BCL 内部会为单值装箱成一个 string[]），
        // 比类型化解析便宜，但不是零分配。
        return headers.TryGetValues(HeaderName, out var values) ? values.FirstOrDefault() : null;
#else
        // NonValidated 直接枚举头里存的原始字符串：既不解析也不分配（HeaderStringValues 是 ref struct，没有索引器）。
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

    /// <summary>
    /// 把原始头值拆成媒体类型主值与字符集：两个取值都可能是 <c>null</c>（无该头 / 无法解析 / 未声明字符集）。
    /// </summary>
    /// <param name="raw">原始头值，可为 <c>null</c>。</param>
    /// <returns>媒体类型主值与字符集，口径与 <see cref="MediaTypeHeaderValue"/> 一致。</returns>
    public static (string? MediaType, string? CharSet) Split(string? raw)
        => string.IsNullOrEmpty(raw)
            ? (null, null)
            : TrySplitSimple(raw.AsSpan(), wantMediaType: true, wantCharSet: true, out var mediaType, out var charSet)
                ? (mediaType, charSet)
                : ParseWithBcl(raw);

    /// <summary>
    /// 只取媒体类型主值：省掉字符集那次子串分配（文本性判定只需要主值）。要解码正文时仍会再取一次字符集，
    /// 所以这条不是「JSON 主路径」——正文转换那一层对主值与字符集都要。
    /// </summary>
    /// <param name="raw">原始头值，可为 <c>null</c>。</param>
    /// <returns>媒体类型主值，或 <c>null</c>。</returns>
    public static string? MediaTypeOf(string? raw)
        => string.IsNullOrEmpty(raw)
            ? null
            : TrySplitSimple(raw.AsSpan(), wantMediaType: true, wantCharSet: false, out var mediaType, out _)
                ? mediaType
                : ParseWithBcl(raw).MediaType;

    /// <summary>只取字符集：省掉媒体类型那次子串分配（纯文本解码只用得到字符集）。</summary>
    /// <param name="raw">原始头值，可为 <c>null</c>。</param>
    /// <returns>字符集，或 <c>null</c>。</returns>
    public static string? CharSetOf(string? raw)
        => string.IsNullOrEmpty(raw)
            ? null
            : TrySplitSimple(raw.AsSpan(), wantMediaType: false, wantCharSet: true, out _, out var charSet)
                ? charSet
                : ParseWithBcl(raw).CharSet;

    /// <summary>权威解析：快路径不认识的形态一律交回 BCL，保证结果与旧实现逐字一致。</summary>
    private static (string? MediaType, string? CharSet) ParseWithBcl(string raw)
        => MediaTypeHeaderValue.TryParse(raw, out var parsed) ? (parsed.MediaType, parsed.CharSet) : (null, null);

    /// <summary>
    /// 简单形态的快路径：<c>type/subtype *( OWS ";" OWS name OWS "=" OWS value )</c>，名字与取值都是 token。
    /// 单趟扫描：任何一处不像 token（引号、转义、逗号、空段、多余等号、非 ASCII）都返回 <c>false</c>，
    /// 由调用方回落 BCL。
    /// </summary>
    /// <param name="raw">原始头值（非空）。</param>
    /// <param name="wantMediaType">是否要切出媒体类型子串（只要字符集的调用方不必为它付费）。</param>
    /// <param name="wantCharSet">是否要切出字符集子串。</param>
    /// <param name="mediaType">拆出的媒体类型主值；未要求或快路径失败时为 <c>null</c>。</param>
    /// <param name="charSet">拆出的字符集（取第一个 <c>charset</c> 参数）；未声明或快路径失败时为 <c>null</c>。</param>
    /// <returns>是否走通了快路径。</returns>
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
            // 没有 '/'、或某一半为空：交给 BCL 判（它同样判为解析失败）。
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
                // 空段 / 尾随 ';'：BCL 判为解析失败，快路径不能当成「没有参数」。
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
                // 空取值（"charset="）：BCL 那边同样解析失败 / 取不到值，交给它定。
                return false;
            }

            var valueEnd = i;
            SkipWhitespace(raw, ref i);

            // 同名参数取第一个：与 BCL 的 NameValueHeaderValue.Find 一致。
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
                // 引号、转义、逗号、非 token 的尾巴：不猜，交给 BCL。
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
