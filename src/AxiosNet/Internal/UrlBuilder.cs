using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text;
using AxiosNet.Metadata;

namespace AxiosNet.Internal;

/// <summary>
/// 负责 baseURL + url 合并（combineURLs）与 params 序列化（buildURL）。
/// 序列化规则与 axios 默认 paramsSerializer 对齐：数组 <c>a[]=1&amp;a[]=2</c>、嵌套 <c>o[k]=v</c>、null 跳过、方括号不转义。
/// </summary>
internal static class UrlBuilder
{
    /// <summary>合并 baseURL 与 url、拼上查询串，校验并缓存为绝对 http(s) 地址；非法地址抛 <c>ERR_INVALID_URL</c>。</summary>
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

        // 空白（非 null）url 视为调用失误；null 才表示请求 baseAddress 本身。
        if (url is { Length: > 0 } && string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(url, config, message: "url 不能为空白字符串：要请求 baseAddress 本身请显式传 \"/\"（或把 Url 置为 null）。");
        }

        var combined = Combine(baseAddress, url);

        // 自定义序列化器返回的串允许带前导 ?/&，这里统一去掉；回调异常统一成 ERR_BAD_OPTION_VALUE。
        string query;
        try
        {
            query = serializer is null
                ? ParamsSerializer.Serialize(queryParams, config)
                : NormalizeQuery(serializer(queryParams));
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException and not AxiosAotMetadataException)
        {
            throw AxiosException.InvalidOption($"paramsSerializer 抛出了异常，查询串无法生成：{ex.Message}", config, ex);
        }

        // 查询串必须拼在 fragment 之前，否则会落进 fragment 发不到服务端；一次成串，不做切片后再拼接。
        var hashIndex = combined.IndexOf('#');
        var target = hashIndex >= 0 ? combined.AsSpan(0, hashIndex) : combined.AsSpan();
        var fragment = hashIndex >= 0 ? combined.AsSpan(hashIndex) : ReadOnlySpan<char>.Empty;

        string pathAndQuery;
        if (query.Length == 0)
        {
            pathAndQuery = combined;
        }
        else if (target.Length == 0)
        {
            pathAndQuery = string.Concat(query, fragment);
        }
        else if (target[^1] is '?' or '&')
        {
            // url 已以 ? / & 结尾时不能再补分隔符，否则会多出一个空参数。
            pathAndQuery = string.Concat(target, query, fragment);
        }
        else
        {
            pathAndQuery = target.Contains('?')
                ? string.Concat(target, "&", query, fragment)
                : string.Concat(target, "?", query, fragment);
        }

        if (UriCache.TryGetValue(pathAndQuery, out var cached))
        {
            return cached;
        }

        try
        {
            var uri = new Uri(pathAndQuery, UriKind.RelativeOrAbsolute);

            // 必须使用 http/https 绝对地址；IsFile 拦下 Unix 下被解析成 file:/// 的根相对路径。
            if (!uri.IsAbsoluteUri || uri.IsFile || !IsHttpScheme(uri))
            {
                throw AxiosException.InvalidUrl(pathAndQuery, config);
            }

            CacheUri(pathAndQuery, uri);
            return uri;
        }
        catch (UriFormatException ex)
        {
            throw AxiosException.InvalidUrl(pathAndQuery, config, ex);
        }
    }

    /// <summary>解析结果缓存上限：满了整表清空，URL 集合频繁变化时不会无限增长。</summary>
    private const int UriCacheLimit = 256;

    /// <summary>已校验过的绝对地址缓存（<see cref="Uri"/> 不可变，可安全共享）。</summary>
    private static readonly ConcurrentDictionary<string, Uri> UriCache = new(StringComparer.Ordinal);

    /// <summary>缓存已校验的绝对地址；达到上限时整表清空（不做逐项淘汰，胜在简单且不会无界增长）。</summary>
    private static void CacheUri(string key, Uri uri)
    {
        if (UriCache.Count >= UriCacheLimit)
        {
            UriCache.Clear();
        }

        UriCache.TryAdd(key, uri);
    }

    /// <summary>对应 axios isAbsoluteURL + combineURLs。</summary>
    private static string Combine(string? baseAddress, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return baseAddress!.TrimEnd('/');
        }

        // 协议相对 URL（//host/path）：有 baseAddress 时按它的 scheme 补全，否则原样返回交给 Build 报错。
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var separator = baseAddress?.IndexOf("://", StringComparison.Ordinal) ?? -1;
            return separator > 0
                ? string.Concat(baseAddress.AsSpan(0, separator), ":".AsSpan(), url.AsSpan())
                : url;
        }

        if (string.IsNullOrWhiteSpace(baseAddress) || IsAbsoluteUrl(url))
        {
            return url;
        }

        return string.Concat(baseAddress.AsSpan().TrimEnd('/'), "/", url.AsSpan().TrimStart('/'));
    }

    /// <summary>是否为绝对地址（形如 <c>scheme://</c>）：要求 <c>scheme</c> 后紧跟 <c>//</c>，避免把「主机:端口」误判。</summary>
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
                // scheme 允许字母、数字与 "+" / "-" / "."，且必须是 scheme://
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

/// <summary>
/// params 序列化（buildURL 的默认实现）：对象 / 字典 / 集合 / JSON 树展平成查询串，
/// null 跳过，键按 encodeURIComponent 转义且保留方括号。
/// </summary>
internal static class ParamsSerializer
{
    /// <summary>把查询参数序列化成查询串：对象 / 字典 / 集合 / 已序列化的字符串原样透传。</summary>
    public static string Serialize(object? paramsObj, AxiosConfig? config = null)
    {
        if (paramsObj is null)
        {
            return string.Empty;
        }

        // 已经是查询串（`"a=1&b=2"`）：原样透传，不走反射。
        if (paramsObj is string rawQuery)
        {
            return NormalizeRawQuery(rawQuery);
        }

        try
        {
            var pairs = new List<KeyValuePair<string, string>>(8);
            var format = config?.SerializationOrNull?.CollectionFormat ?? AxiosCollectionFormat.Brackets;
            if (paramsObj is JsonElement rootElement)
            {
                // JsonElement 按 JSON 树展开，不走反射。
                if (rootElement.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    or JsonValueKind.True or JsonValueKind.False)
                {
                    throw InvalidParamsRoot(paramsObj, config);
                }

                AppendJsonElement(null, rootElement, pairs, format);
            }
            else if (paramsObj is IDictionary<string, object> stringKeyedDictionary)
            {
                // ExpandoObject（dynamic）等仅实现泛型字典的类型
                foreach (var entry in stringKeyedDictionary)
                {
                    Append(EscapeKey(entry.Key), entry.Value, pairs, visited: null, format);
                }
            }
            else if (paramsObj is IReadOnlyDictionary<string, object> readOnlyDictionary)
            {
                // 只实现 IReadOnlyDictionary 的字典：按键值对展开，不走 POCO 反射。
                foreach (var entry in readOnlyDictionary)
                {
                    Append(EscapeKey(entry.Key), entry.Value, pairs, visited: null, format);
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

                    Append(EscapeKey(entry.Key.ToString()!), entry.Value, pairs, visited: null, format);
                }
            }
            else if (QueryMap.CanEnumerate(paramsObj))
            {
                // 只实现 IDictionary<string,T> 的自定义字典（T 不是 object）或值类型字典：按键值对展开。
                foreach (var entry in QueryMap.Enumerate(paramsObj))
                {
                    Append(EscapeKey(entry.Key), entry.Value, pairs, visited: null, format);
                }
            }
            else if (paramsObj is not byte[] && paramsObj is IEnumerable items)
            {
                // 根集合用下标作键（与表单展平一致）。
                var index = 0;
                foreach (var item in items)
                {
                    Append("[" + index + "]", item, pairs, visited: null, format);
                    index++;
                }
            }
            else if (IsScalar(paramsObj))
            {
                // 标量没有可表达的键名，按非法根类型报错。
                throw InvalidParamsRoot(paramsObj, config);
            }
            else
            {
                // 根对象：形状决定成员顺序与键名；query 展平不跳过 [JsonIgnore]（与表单不同）。
                var shape = ShapeCache.Get(paramsObj.GetType());
                foreach (var member in shape.Members)
                {
                    Append(member.EscapedName, member.GetValue(paramsObj), pairs, visited: null, format);
                }
            }

            // 收尾：用单个 StringBuilder 一次拼出查询串。
            if (pairs.Count == 0)
            {
                return string.Empty;
            }

            var capacity = pairs.Count * 2;
            for (var i = 0; i < pairs.Count; i++)
            {
                capacity += pairs[i].Key.Length + pairs[i].Value.Length;
            }

            var query = new StringBuilder(capacity);
            for (var i = 0; i < pairs.Count; i++)
            {
                if (i > 0)
                {
                    query.Append('&');
                }

                query.Append(pairs[i].Key).Append('=').Append(pairs[i].Value);
            }

            return query.ToString();
        }
        catch (DataCycleException ex)
        {
            // 环检测：转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
    }

    /// <summary>非法根类型：统一成 <c>ERR_BAD_OPTION_VALUE</c>，并说明可用的形态。</summary>
    private static AxiosException InvalidParamsRoot(object paramsObj, AxiosConfig? config)
        => AxiosException.InvalidOption(
            $"params 的类型 '{paramsObj.GetType()}' 无法序列化为查询串：请传对象 / 字典 / 集合，"
            + "或已经序列化好的查询串（string，如 \"a=1&b=2\"）。", config);

    /// <summary>去掉查询串首尾空白与前导 <c>?</c> / <c>&amp;</c>（与自定义 paramsSerializer 的归一化一致）。</summary>
    private static string NormalizeRawQuery(string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed.TrimStart('?', '&');
    }

    /// <summary>把单个值按实际形态展平成查询键值对：null 跳过，字典 / 集合 / JSON 树 / 标量 / 嵌套对象分别处理。</summary>
    private static void Append(
        string prefix,
        object? value,
        List<KeyValuePair<string, string>> pairs,
        HashSet<object>? visited,
        AxiosCollectionFormat format)
    {
        // 与 axios 一致：null 直接跳过
        if (value is null)
        {
            return;
        }

        if (value is JsonElement element)
        {
            AppendJsonElement(prefix, element, pairs, format);
            return;
        }

        if (value is IDictionary<string, object> stringKeyedNested)
        {
            // 嵌套 ExpandoObject（dynamic）：prefix[key]=value
            visited = ObjectGraph.Enter(visited, stringKeyedNested);
            try
            {
                foreach (var entry in stringKeyedNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs, visited, format);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, stringKeyedNested);
            }

            return;
        }

        // 只实现 IReadOnlyDictionary 的字典：按键值对展开，不走 POCO 反射。
        if (value is IReadOnlyDictionary<string, object> readOnlyNested)
        {
            visited = ObjectGraph.Enter(visited, readOnlyNested);
            try
            {
                foreach (var entry in readOnlyNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs, visited, format);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, readOnlyNested);
            }

            return;
        }

        if (value is IDictionary dict)
        {
            visited = ObjectGraph.Enter(visited, dict);
            try
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is null)
                    {
                        continue;
                    }

                    Append($"{prefix}[{EscapeKey(entry.Key.ToString()!)}]", entry.Value, pairs, visited, format);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, dict);
            }

            return;
        }

        // 只实现 IDictionary<string,T> 的自定义字典（T 不是 object）或值类型字典：按键值对展开。
        if (value is IEnumerable mapValue && value is not string && value is not byte[] && QueryMap.CanEnumerate(mapValue))
        {
            visited = ObjectGraph.Enter(visited, mapValue);
            try
            {
                foreach (var entry in QueryMap.Enumerate(mapValue))
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs, visited, format);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, mapValue);
            }

            return;
        }

        if (value is not string && value is not byte[] && value is IEnumerable enumerable)
        {
            if (format != AxiosCollectionFormat.Brackets)
            {
                AppendCollection(prefix, enumerable, pairs, visited, format);
                return;
            }

            visited = ObjectGraph.Enter(visited, value);
            try
            {
                foreach (var item in enumerable)
                {
                    Append($"{prefix}[]", item, pairs, visited, format);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, value);
            }

            return;
        }

        if (IsScalar(value))
        {
            pairs.Add(new KeyValuePair<string, string>(prefix, EscapeValue(ScalarToString(value))));
            return;
        }

        // 嵌套对象（匿名类型 / record / POCO）：形状决定成员，序列化为 prefix[prop]=value（不跳过 [JsonIgnore]）。
        visited = ObjectGraph.Enter(visited, value);
        try
        {
            var shape = ShapeCache.Get(value.GetType(), prefix);
            foreach (var member in shape.Members)
            {
                // 后缀（含转义）在形状里预计算，这里只需一次前缀拼接。
                Append(prefix + member.BracketSuffix, member.GetValue(value), pairs, visited, format);
            }
        }
        finally
        {
            ObjectGraph.Exit(visited, value);
        }
    }

    /// <summary>
    /// 非默认集合形态：<see cref="AxiosCollectionFormat.Multi"/> 展开成重复键，其余分隔形态把标量元素拼成一个值
    /// （元素含对象时回落到方括号形态）。
    /// </summary>
    private static void AppendCollection(
        string prefix,
        IEnumerable enumerable,
        List<KeyValuePair<string, string>> pairs,
        HashSet<object>? visited,
        AxiosCollectionFormat format)
    {
        visited = ObjectGraph.Enter(visited, enumerable);
        try
        {
            // Multi 直接流式枚举；其余分隔形态需物化一次（先判全标量，再拼接或回落）。
            if (format == AxiosCollectionFormat.Multi)
            {
                foreach (var item in enumerable)
                {
                    Append(prefix, item, pairs, visited, format);
                }

                return;
            }

            var items = enumerable as ICollection is { Count: > 0 } counted
                ? new List<object?>(counted.Count)
                : new List<object?>();
            foreach (var item in enumerable)
            {
                items.Add(item);
            }

            var parts = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (item is null)
                {
                    continue;
                }

                if (!IsScalar(item))
                {
                    foreach (var fallback in items)
                    {
                        Append($"{prefix}[]", fallback, pairs, visited, format);
                    }

                    return;
                }

                parts.Add(ScalarToString(item));
            }

            if (parts.Count > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(prefix, EscapeValue(string.Join(Separator(format), parts))));
            }
        }
        finally
        {
            ObjectGraph.Exit(visited, enumerable);
        }
    }

    /// <summary>分隔形态的分隔符（未列出的形态按逗号处理）。</summary>
    private static string Separator(AxiosCollectionFormat format) => format switch
    {
        AxiosCollectionFormat.Ssv => " ",
        AxiosCollectionFormat.Tsv => "\t",
        AxiosCollectionFormat.Pipes => "|",
        _ => ","
    };

    /// <summary>按 JSON 树展开查询参数：嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组用 <c>[i]</c>）、null 跳过。</summary>
    private static void AppendJsonElement(
        string? prefix,
        JsonElement element,
        List<KeyValuePair<string, string>> pairs,
        AxiosCollectionFormat format)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElement(prefix is null ? EscapeKey(property.Name) : prefix + "[" + EscapeKey(property.Name) + "]", property.Value, pairs, format);
                }

                break;

            case JsonValueKind.Array:
            {
                if (format != AxiosCollectionFormat.Brackets)
                {
                    AppendJsonArray(prefix, element, pairs, format);
                    break;
                }

                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElement(prefix is null ? "[" + index + "]" : prefix + "[]", item, pairs, format);
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

    /// <summary>
    /// JSON 树的非默认集合形态：Multi 重复键，其余拼成一个值（元素含对象/数组或根数组时回落方括号形态）。
    /// </summary>
    private static void AppendJsonArray(
        string? prefix,
        JsonElement element,
        List<KeyValuePair<string, string>> pairs,
        AxiosCollectionFormat format)
    {
        if (prefix is not null)
        {
            if (format == AxiosCollectionFormat.Multi)
            {
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElement(prefix, item, pairs, format);
                }

                return;
            }

            List<string>? parts = new();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
                    or JsonValueKind.True or JsonValueKind.False))
                {
                    parts = null;
                    break;
                }

                parts.Add(JsonScalarText(item));
            }

            if (parts is { Count: > 0 })
            {
                pairs.Add(new KeyValuePair<string, string>(prefix, EscapeValue(string.Join(Separator(format), parts))));
                return;
            }

            if (parts is { Count: 0 })
            {
                return;
            }
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            AppendJsonElement(prefix is null ? "[" + index + "]" : prefix + "[]", item, pairs, format);
            index++;
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
        // byte[] 在 query 里走 base64（可逆），不算「标量」参与 Path/Header 校验
        => value is byte[] || ScalarTypes.IsScalarType(value.GetType());

    /// <summary>标量 → 线上文本：bool 是 <c>true</c> / <c>false</c>，日期时间是 UTC 毫秒 ISO 8601。</summary>
    internal static string ScalarToString(object value) => value switch
    {
        // query 里没有 byte[] 的标准形态，用 base64（可逆）。
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => BodyFields.ScalarToString(value)
    };

    // 同 axios 默认 encoder：encodeURIComponent，并把 %5B / %5D 还原成 [ ]
    internal static string EscapeKey(string key)
        => Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]");

    private static string EscapeValue(string value)
        => Uri.EscapeDataString(value);
}
