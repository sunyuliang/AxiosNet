using System.Globalization;

namespace AxiosNet.Cache;

/// <summary>RFC 9111 的响应头语义：把 <c>Cache-Control</c> / <c>Date</c> / <c>Age</c> / <c>Expires</c> / <c>Vary</c> 读成可判定的新鲜期信息。纯静态、不持有状态。</summary>
internal static class HttpCacheSemantics
{
    /// <summary>解析响应头里与缓存相关的全部信息；只有解析模式或按需读取策略的存储才会走到这里。</summary>
    internal static HttpCacheInfo ReadHttpCacheInfo(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        DateTimeOffset receivedAt)
    {
        var cacheControl = CacheControlDirectives.Parse(GetValues(headers, "Cache-Control"));
        var date = ParseHttpDate(FirstValue(headers, "Date"));
        var expiresText = FirstValue(headers, "Expires");
        var expires = ParseHttpDate(expiresText);

        // RFC 9111 的 current_age：Age 头与「收到响应 - Date」取较大值，负数按零。
        var age = ParseSeconds(FirstValue(headers, "Age")) ?? TimeSpan.Zero;
        if (date is { } parsedDate && receivedAt > parsedDate)
        {
            var apparentAge = receivedAt - parsedDate;
            if (apparentAge > age)
            {
                age = apparentAge;
            }
        }

        TimeSpan? freshness = null;
        if (cacheControl.MaxAge is { } maxAge)
        {
            freshness = maxAge;
        }
        else if (expiresText is not null)
        {
            // 非法 Expires 按「已过期」处理（RFC 9111 §5.3）；合法值的新鲜期是 Expires - Date，缺 Date 时用收到响应的时刻。
            var reference = date ?? receivedAt;
            freshness = expires is { } parsedExpires && parsedExpires > reference
                ? parsedExpires - reference
                : TimeSpan.Zero;
        }

        return new HttpCacheInfo(freshness, age, cacheControl, date, expires, ParseVary(headers));
    }

    /// <summary><c>Vary</c> 里只要含 <c>*</c> 就表示键覆盖不住，任何模式都不落库。</summary>
    internal static bool ContainsVaryStar(IReadOnlyList<string> vary)
    {
        foreach (var field in vary)
        {
            if (field == "*")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析 <c>max-age</c> 这类秒数；<c>null</c>、负数、带符号、非整数一律当无效。超出 <see cref="TimeSpan"/> 上限按「无限长」。</summary>
    internal static TimeSpan? ParseSeconds(string? value)
    {
        if (value is null
            || !long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return null;
        }

        // 秒数换算成 tick 可能超出 TimeSpan 上限：超出就按「无限长」处理。
        return seconds > long.MaxValue / TimeSpan.TicksPerSecond
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }

    // 取某个响应头的全部值；头不存在返回 null。
    private static IEnumerable<string>? GetValues(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string name)
        => headers.TryGetValue(name, out var values) ? values : null;

    // 取第一个非空白值，同名头按声明顺序。
    private static string? FirstValue(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string name)
        => GetValues(headers, name)?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    // 解析 HTTP 日期；空白或不合法返回 null。
    private static DateTimeOffset? ParseHttpDate(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
            ? parsed
            : null;

    // 拆 Vary 为字段名列表并去重；只经 AxiosCacheEntryPolicy 交给存储，库自身的键不读它。
    private static IReadOnlyList<string> ParseVary(IReadOnlyDictionary<string, IEnumerable<string>> headers)
    {
        var values = GetValues(headers, "Vary");
        if (values is null)
        {
            return Array.Empty<string>();
        }

        List<string>? fields = null;
        foreach (var value in values)
        {
            foreach (var part in value.Split(','))
            {
                var field = part.Trim();
                if (field.Length > 0)
                {
                    (fields ??= new List<string>()).Add(field);
                }
            }
        }

        return fields ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
