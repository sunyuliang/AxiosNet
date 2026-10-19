using System.Collections;
using System.Globalization;

namespace AxiosNet.Internal;

/// <summary>
/// 负责 baseURL + url 合并（combineURLs）以及 params 序列化（buildURL）。
/// 序列化规则与 axios 默认 paramsSerializer 对齐：
/// 数组 a[]=1&amp;a[]=2、嵌套对象 o[k]=v、null 值跳过、方括号不转义。
/// </summary>
internal static class UrlBuilder
{
    public static Uri Build(
        string? baseAddress,
        string? url,
        object? queryParams,
        Func<object?, string>? serializer = null)
    {
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(baseAddress))
        {
            throw AxiosException.InvalidUrl(url ?? string.Empty);
        }

        var combined = Combine(baseAddress, url);

        // 自定义序列化器（对应 axios paramsSerializer）返回的串允许带前导 ?/&，这里统一去掉。
        var query = serializer is null
            ? ParamsSerializer.Serialize(queryParams)
            : NormalizeQuery(serializer(queryParams));
        if (query.Length > 0)
        {
            var separator = combined.Contains('?') ? "&" : "?";
            combined = combined + separator + query;
        }

        try
        {
            var uri = new Uri(combined, UriKind.RelativeOrAbsolute);

            // 连接池必须使用绝对地址；相对 URL 且未提供 baseURL 时统一抛 ERR_INVALID_URL，
            // 而不是等到连接池抛出 InvalidOperationException。
            // IsFile：Unix 下 "/items" 这类根相对地址会被 Uri 解析成 file:///items（隐式文件路径），
            // 不在这里拦住的话，会一路走到 Handler 才报「不支持的 scheme」。
            if (!uri.IsAbsoluteUri || uri.IsFile)
            {
                throw AxiosException.InvalidUrl(combined);
            }

            return uri;
        }
        catch (UriFormatException ex)
        {
            throw AxiosException.InvalidUrl(combined, ex);
        }
    }

    /// <summary>对应 axios isAbsoluteURL + combineURLs。</summary>
    private static string Combine(string? baseAddress, string? url)    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return baseAddress!.TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(baseAddress) || IsAbsoluteUrl(url))
        {
            return url;
        }

        return baseAddress.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    /// <summary>
    /// 是否为绝对地址（形如 <c>scheme:</c>）。
    /// <para>
    /// 不用 <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> 判断：Unix 下以 <c>/</c> 开头的路径
    /// 会被 Uri 当成绝对文件路径（<c>file:///items</c>），从而把根相对地址误判为绝对地址
    /// （dotnet/runtime#76816，官方明确为 by design）。显式的 <c>file://</c> 仍按绝对地址处理，
    /// 由 <see cref="Build"/> 统一拒绝。
    /// </para>
    /// </summary>
    private static bool IsAbsoluteUrl(string url)
    {
        if (url.Length == 0 || !IsAsciiLetter(url[0]))
        {
            return false;
        }

        for (var i = 1; i < url.Length; i++)
        {
            var c = url[i];
            if (c == ':')
            {
                return true;
            }

            // RFC 3986 scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
            if (!IsAsciiLetter(c) && (c < '0' || c > '9') && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return query.Trim().TrimStart('?', '&');
    }
}

internal static class ParamsSerializer
{
    public static string Serialize(object? paramsObj)
    {
        if (paramsObj is null)
        {
            return string.Empty;
        }

        var pairs = new List<KeyValuePair<string, string>>(8);
        if (paramsObj is IDictionary<string, object> stringKeyedDictionary)
        {
            // ExpandoObject（dynamic）等仅实现泛型字典的类型
            foreach (var entry in stringKeyedDictionary)
            {
                Append(EscapeKey(entry.Key), entry.Value, pairs);
            }
        }
        else if (paramsObj is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                Append(EscapeKey(entry.Key.ToString()!), entry.Value, pairs);
            }
        }
        else
        {
            foreach (var property in ReflectionCache.GetReadableProperties(paramsObj.GetType()))
            {
                Append(EscapeKey(property.Name), property.GetValue(paramsObj), pairs);
            }
        }

        return string.Join("&", pairs.Select(p => p.Key + "=" + p.Value));
    }

    private static void Append(string prefix, object? value, List<KeyValuePair<string, string>> pairs)
    {
        // 与 axios 一致：null 直接跳过
        if (value is null)
        {
            return;
        }

        if (value is IDictionary<string, object> stringKeyedNested)
        {
            // 嵌套 ExpandoObject（dynamic）：prefix[key]=value
            foreach (var entry in stringKeyedNested)
            {
                Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs);
            }

            return;
        }

        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                Append($"{prefix}[{EscapeKey(entry.Key.ToString()!)}]", entry.Value, pairs);
            }

            return;
        }

        if (value is not string && value is not byte[] && value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Append($"{prefix}[]", item, pairs);
            }

            return;
        }

        if (IsScalar(value))
        {
            pairs.Add(new KeyValuePair<string, string>(prefix, EscapeValue(ScalarToString(value))));
            return;
        }

        // 嵌套对象（匿名类型 / record / POCO）：反射公共属性，序列化为 prefix[prop]=value
        foreach (var property in ReflectionCache.GetReadableProperties(value.GetType()))
        {
            Append($"{prefix}[{EscapeKey(property.Name)}]", property.GetValue(value), pairs);
        }
    }

    private static bool IsScalar(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive
            || type.IsEnum
            || value is string
            || value is byte[]
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is DateOnly
            || value is TimeOnly
            || value is TimeSpan
            || value is Guid
            || value is Uri;
    }

    private static string ScalarToString(object value)
    {
        switch (value)
        {
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            case Enum e:
                return e.ToString();
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    // axios 默认 encoder 使用 encodeURIComponent，并把 %5B / %5D 还原成 [ ]
    private static string EscapeKey(string key)
        => Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]");

    private static string EscapeValue(string value)
        => Uri.EscapeDataString(value);
}
