using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 负责 baseURL + url 合并（combineURLs）以及 params 序列化（buildURL）。
/// 序列化规则与 axios 默认 paramsSerializer 对齐：
/// 数组 a[]=1&amp;a[]=2、嵌套对象 o[k]=v、null 值跳过、方括号不转义。
/// <para>
/// 百分号编码的非保留字符（<c>%41</c>→<c>A</c>、<c>%7E</c>→<c>~</c>）会被
/// <see cref="System.Uri"/> 按 RFC 3986 §6.2.2.2 归一化——这与 HttpClient 实际发上线的
/// 请求行完全一致（已用原始 socket 验证），因此 <see cref="AxiosClient.GetUri(AxiosConfig)"/> 返回的
/// 就是真实发送形态；需要按签名原样保留调用方传入的编码串时，请用自定义 paramsSerializer
/// 输出最终串，并以 <see cref="AxiosClient.GetUri(AxiosConfig)"/> 的结果作为签名输入。
/// </para>
/// </summary>
internal static class UrlBuilder
{
    public static Uri Build(
        string? baseAddress,
        string? url,
        object? queryParams,
        Func<object?, string>? serializer = null,
        AxiosConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(baseAddress))
        {
            throw AxiosException.InvalidUrl(url ?? string.Empty, config);
        }

        var combined = Combine(baseAddress, url);

        // 查询串必须拼在 fragment 之前：url 自带 "#top" 时直接追加会变成
        // "http://a.com/p#top?q=1"——整个查询串落进 fragment，永远发不到服务端
        // （axios 的 buildURL 也是先切 hash、拼完再补回）。
        var hashIndex = combined.IndexOf('#');
        var fragment = hashIndex >= 0 ? combined[hashIndex..] : string.Empty;
        var target = hashIndex >= 0 ? combined[..hashIndex] : combined;

        // 自定义序列化器（对应 axios paramsSerializer）返回的串允许带前导 ?/&，这里统一去掉。
        var query = serializer is null
            ? ParamsSerializer.Serialize(queryParams, config)
            : NormalizeQuery(serializer(queryParams));
        if (query.Length > 0)
        {
            // 「url 已经以 ? / & 结尾」时不能再补分隔符：`search?` + params 会拼出 `search?&a=1`，
            // 多出来的空参数在严格的服务端/网关眼里是「一个无名的空参数」，不是等价形态。
            if (target.Length == 0)
            {
                target = query;
            }
            else if (target[^1] is '?' or '&')
            {
                target += query;
            }
            else
            {
                target += (target.Contains('?') ? "&" : "?") + query;
            }
        }

        combined = fragment.Length > 0 ? target + fragment : target;

        try
        {
            var uri = new Uri(combined, UriKind.RelativeOrAbsolute);

            // 连接池必须使用绝对地址；相对 URL 且未提供 baseURL 时统一抛 ERR_INVALID_URL，
            // 而不是等到连接池抛出 InvalidOperationException。
            // IsFile：Unix 下 "/items" 这类根相对地址会被 Uri 解析成 file:///items（隐式文件路径），
            // 不在这里拦住的话，会一路走到 Handler 才报「不支持的 scheme」。
            if (!uri.IsAbsoluteUri || uri.IsFile || !IsHttpScheme(uri))
            {
                throw AxiosException.InvalidUrl(combined, config);
            }

            return uri;
        }
        catch (UriFormatException ex)
        {
            throw AxiosException.InvalidUrl(combined, config, ex);
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
    /// 是否为绝对地址（形如 <c>scheme://</c>）。
    /// <para>
    /// 不用 <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> 判断：Unix 下以 <c>/</c> 开头的路径
    /// 会被 Uri 当成绝对文件路径（<c>file:///items</c>），从而把根相对地址误判为绝对地址
    /// （dotnet/runtime#76816，官方明确为 by design）。显式的 <c>file://</c> 仍按绝对地址处理，
    /// 由 <see cref="Build"/> 统一拒绝。
    /// </para>
    /// <para>
    /// 判定要求 <c>scheme</c> 后紧跟 <c>//</c>（与 axios 的 <c>isAbsoluteURL</c> 一致）。
    /// 只按 RFC 3986 的 <c>scheme ":"</c> 判定会把「主机:端口」形态的相对路径
    /// （<c>localhost:8080/items</c>、<c>api.example.com:8443/x</c>）当成绝对地址：
    /// baseURL 被静默绕过，随后又因 scheme 不是 http(s) 抛 <c>ERR_INVALID_URL</c>。
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
                // RFC 3986 scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )，且必须是 scheme://
                return url.Length > i + 2 && url[i + 1] == '/' && url[i + 2] == '/';
            }

            if (!IsAsciiLetter(c) && (c < '0' || c > '9') && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>只有 http/https 能交给传输层，其余（ftp/data/ws…）在构造阶段就拒绝。</summary>
    private static bool IsHttpScheme(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

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
    /// <param name="paramsObj">查询参数：对象 / 字典 / 集合 / 已序列化的查询串（string）。</param>
    /// <param name="config">用于把「params 类型非法」的错误挂上配置。</param>
    public static string Serialize(object? paramsObj, AxiosConfig? config = null)
    {
        if (paramsObj is null)
        {
            return string.Empty;
        }

        // 已经是查询串（`"a=1&b=2"`）：原样透传。此前落到反射分支后会被当成普通对象序列化成
        // `?Length=7`——把 string 自身的 CLR 属性写进了查询串，与服务端约定完全不搭。
        if (paramsObj is string rawQuery)
        {
            return NormalizeRawQuery(rawQuery);
        }

        var pairs = new List<KeyValuePair<string, string>>(8);
        if (paramsObj is JsonElement rootElement)
        {
            // 直接传 JsonElement（从 JsonDocument / JsonSerializer 拿到的查询片段）必须按 JSON 树展开；
            // 走反射兜底只会把 JsonElement 自己的属性（ValueKind / RawText…）序列化出去。
            if (rootElement.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False)
            {
                throw InvalidParamsRoot(paramsObj, config);
            }

            AppendJsonElement(null, rootElement, pairs);
        }
        else if (paramsObj is IDictionary<string, object> stringKeyedDictionary)
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
        else if (paramsObj is not byte[] && paramsObj is IEnumerable items)
        {
            // 根集合（数组 / List）：与表单展平一致，用下标作键。
            // 此前同样落到反射分支，会输出 `Length=2&Rank=1&SyncRoot[]=1&...`（Array 的 CLR 属性）。
            var index = 0;
            foreach (var item in items)
            {
                Append("[" + index + "]", item, pairs);
                index++;
            }
        }
        else if (IsScalar(paramsObj))
        {
            // 标量没有任何可表达的键名：伪造一个 key 或静默丢弃都会让调用方以为参数发出去了。
            throw InvalidParamsRoot(paramsObj, config);
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

    /// <summary>非法根类型：统一成 <c>ERR_BAD_OPTION_VALUE</c>，并说明可用的形态。</summary>
    private static AxiosException InvalidParamsRoot(object paramsObj, AxiosConfig? config)
        => AxiosException.InvalidOption(
            $"params 的类型 '{paramsObj.GetType()}' 无法序列化为查询串：请传对象 / 字典 / 集合，"
            + "或已经序列化好的查询串（string，如 \"a=1&b=2\"）。",
            config);

    /// <summary>去掉查询串首尾空白与前导 <c>?</c> / <c>&amp;</c>（与自定义 paramsSerializer 的归一化一致）。</summary>
    private static string NormalizeRawQuery(string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed.TrimStart('?', '&');
    }

    private static void Append(string prefix, object? value, List<KeyValuePair<string, string>> pairs)
    {
        // 与 axios 一致：null 直接跳过
        if (value is null)
        {
            return;
        }

        if (value is JsonElement element)
        {
            AppendJsonElement(prefix, element, pairs);
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

    /// <summary>
    /// 按 JSON 树展开查询参数，规则与表单展平一致：
    /// 嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组用 <c>[i]</c>）、null 跳过。
    /// </summary>
    private static void AppendJsonElement(string? prefix, JsonElement element, List<KeyValuePair<string, string>> pairs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElement(
                        prefix is null ? EscapeKey(property.Name) : prefix + "[" + EscapeKey(property.Name) + "]",
                        property.Value,
                        pairs);
                }

                break;

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElement(prefix is null ? "[" + index + "]" : prefix + "[]", item, pairs);
                    index++;
                }

                break;
            }

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (prefix is null)
                {
                    // 根标量没有可表达的键名，忽略而不是伪造一个 key。
                    break;
                }

                pairs.Add(new KeyValuePair<string, string>(prefix, EscapeValue(JsonScalarText(element))));
                break;

            default:
                // null / undefined：与 axios 一致，直接跳过。
                break;
        }
    }

    private static string JsonScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText()
    };

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
            case byte[] bytes:
                // query 里没有 byte[] 的标准形态；base64 至少可逆且能完整还原
                return Convert.ToBase64String(bytes);
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
