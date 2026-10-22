using System.Collections;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 负责 baseURL + url 合并（combineURLs）与 params 序列化（buildURL）。
/// 序列化规则与 axios 默认 paramsSerializer 对齐：数组 <c>a[]=1&amp;a[]=2</c>、嵌套 <c>o[k]=v</c>、
/// null 跳过、方括号不转义。
/// <para>
/// <see cref="System.Uri"/> 会按 RFC 3986 归一化非保留字符（<c>%41</c>→<c>A</c>），与 HttpClient 实际发送的
/// 请求行一致，因此 <see cref="AxiosClient.GetUri(AxiosConfig)"/> 返回的就是真实发送形态。
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

        // 空白（非 null）url 是调用方的失误：静默解释成「请求 baseAddress 本身」会让
        // 一次「变量忘赋值」变成向根路径发出的意外请求（POST / 可能带副作用）。
        // null 仍然合法（= 请求 baseAddress，与快捷方法显式传 "/" 的语义并存）。
        if (url is { Length: > 0 } && string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(
                url,
                config,
                message: "url 不能为空白字符串：要请求 baseAddress 本身请显式传 \"/\"（或把 Url 置为 null）。");
        }

        var combined = Combine(baseAddress, url);

        // 查询串必须拼在 fragment 之前：url 自带 "#top" 时直接追加会让整个查询串落进 fragment，
        // 永远发不到服务端（axios 的 buildURL 也是先切 hash、拼完再补回）。
        var hashIndex = combined.IndexOf('#');
        var fragment = hashIndex >= 0 ? combined[hashIndex..] : string.Empty;
        var target = hashIndex >= 0 ? combined[..hashIndex] : combined;

        // 自定义序列化器（对应 axios paramsSerializer）返回的串允许带前导 ?/&，这里统一去掉。
        // 调用方回调抛的异常属于配置错误：统一成 ERR_BAD_OPTION_VALUE，不让裸异常从管线漏出
        // （与「params 根类型非法」同一个错误码）。
        string query;
        try
        {
            query = serializer is null
                ? ParamsSerializer.Serialize(queryParams, config)
                : NormalizeQuery(serializer(queryParams));
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.InvalidOption(
                $"paramsSerializer 抛出了异常，查询串无法生成：{ex.Message}", config, ex);
        }
        if (query.Length > 0)
        {
            // 「url 已以 ? / & 结尾」时不能再补分隔符：`search?` + params 会拼出 `search?&a=1`，
            // 多出的空参数在严格的服务端/网关眼里是「一个无名的空参数」。
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

            // 连接池必须使用绝对地址；相对 URL 且未提供 baseURL 时统一抛 ERR_INVALID_URL。
            // IsFile：Unix 下 "/items" 会被 Uri 解析成 file:///items，不拦住会一路走到 Handler 才报错。
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
    private static string Combine(string? baseAddress, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return baseAddress!.TrimEnd('/');
        }

        // 协议相对 URL（//host/path）：axios 的 isAbsoluteURL 把它算绝对地址，浏览器按页面协议补全。
        // 库没有页面协议，但有 baseAddress 时按它的 scheme 补全（两者都缺时原样返回，
        // 交给 Build 抛 ERR_INVALID_URL —— 没有 scheme 的 //host 在任何 HTTP 栈里都解析不了）。
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

        return baseAddress.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    /// <summary>
    /// 是否为绝对地址（形如 <c>scheme://</c>）。
    /// <para>
    /// 不用 <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>：Unix 下以 <c>/</c> 开头的路径会被当成
    /// 绝对文件路径（<c>file:///items</c>），把根相对地址误判为绝对地址（dotnet/runtime#76816，by design）。
    /// </para>
    /// <para>
    /// 要求 <c>scheme</c> 后紧跟 <c>//</c>（与 axios 一致）：只按 <c>scheme ":"</c> 判定会把
    /// 「主机:端口」形态（<c>localhost:8080/items</c>）当成绝对地址，baseURL 被静默绕过。
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

        // 已经是查询串（`"a=1&b=2"`）：原样透传，否则落到反射分支会被序列化成 `?Length=7`。
        if (paramsObj is string rawQuery)
        {
            return NormalizeRawQuery(rawQuery);
        }

        try
        {
            var pairs = new List<KeyValuePair<string, string>>(8);
            if (paramsObj is JsonElement rootElement)
            {
                // 直接传 JsonElement 必须按 JSON 树展开；走反射兜底只会把 ValueKind / RawText 序列化出去。
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
                    Append(EscapeKey(entry.Key), entry.Value, pairs, visited: null);
                }
            }
            else if (paramsObj is IReadOnlyDictionary<string, object> readOnlyDictionary)
            {
                // 只实现 IReadOnlyDictionary 的字典：按键值对展开，否则会被当 POCO 反射出 Key/Value 字段。
                foreach (var entry in readOnlyDictionary)
                {
                    Append(EscapeKey(entry.Key), entry.Value, pairs, visited: null);
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

                    Append(EscapeKey(entry.Key.ToString()!), entry.Value, pairs, visited: null);
                }
            }
            else if (paramsObj is not byte[] && paramsObj is IEnumerable items)
            {
                // 根集合（数组 / List）：与表单展平一致，用下标作键；否则反射分支会输出 Array 的 CLR 属性。
                var index = 0;
                foreach (var item in items)
                {
                    Append("[" + index + "]", item, pairs, visited: null);
                    index++;
                }
            }
            else if (IsScalar(paramsObj))
            {
                // 标量没有可表达的键名：伪造 key 或静默丢弃都会让调用方以为参数发出去了。
                throw InvalidParamsRoot(paramsObj, config);
            }
            else
            {
                foreach (var property in ReflectionCache.GetReadableProperties(paramsObj.GetType()))
                {
                    Append(EscapeKey(property.Name), property.GetValue(paramsObj), pairs, visited: null);
                }
            }

            return string.Join("&", pairs.Select(p => p.Key + "=" + p.Value));
        }
        catch (DataCycleException ex)
        {
            // 环检测：无界递归会栈溢出（不可捕获），转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
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

    private static void Append(
        string prefix,
        object? value,
        List<KeyValuePair<string, string>> pairs,
        HashSet<object>? visited)
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
            visited = ObjectGraph.Enter(visited, stringKeyedNested);
            try
            {
                foreach (var entry in stringKeyedNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs, visited);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, stringKeyedNested);
            }

            return;
        }

        // 只实现 IReadOnlyDictionary 的字典：按键值对展开，否则会被当 POCO 反射出 Key/Value 字段。
        if (value is IReadOnlyDictionary<string, object> readOnlyNested)
        {
            visited = ObjectGraph.Enter(visited, readOnlyNested);
            try
            {
                foreach (var entry in readOnlyNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, pairs, visited);
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

                    Append($"{prefix}[{EscapeKey(entry.Key.ToString()!)}]", entry.Value, pairs, visited);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, dict);
            }

            return;
        }

        if (value is not string && value is not byte[] && value is IEnumerable enumerable)
        {
            visited = ObjectGraph.Enter(visited, value);
            try
            {
                foreach (var item in enumerable)
                {
                    Append($"{prefix}[]", item, pairs, visited);
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

        // 嵌套对象（匿名类型 / record / POCO）：反射公共属性，序列化为 prefix[prop]=value
        visited = ObjectGraph.Enter(visited, value);
        try
        {
            foreach (var property in ReflectionCache.GetReadableProperties(value.GetType()))
            {
                Append($"{prefix}[{EscapeKey(property.Name)}]", property.GetValue(value), pairs, visited);
            }
        }
        finally
        {
            ObjectGraph.Exit(visited, value);
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
        // byte[] 在 query 里走 base64（可逆），不算「标量」参与 Path/Header 校验
        => value is byte[] || ScalarTypes.IsScalarType(value.GetType());

    /// <summary>
    /// 标量 → 线上文本。查询串、路径段、请求头共用这一份规则：
    /// bool 是 <c>true</c> / <c>false</c>，日期时间是 UTC 毫秒 ISO 8601 ——
    /// 三处各写一份时 bool 会退化成 <c>True</c>（Boolean 不实现 IFormattable），
    /// 同一个值在 <c>[Query]</c> 与 <c>[Path]</c> 上因此发出两种形态。
    /// </summary>
    internal static string ScalarToString(object value) => value switch
    {
        // query 里没有 byte[] 的标准形态；base64 至少可逆且能完整还原
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => BodyFields.ScalarToString(value)
    };

    // axios 默认 encoder 用 encodeURIComponent，并把 %5B / %5D 还原成 [ ]
    private static string EscapeKey(string key)
        => Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]");

    private static string EscapeValue(string value)
        => Uri.EscapeDataString(value);
}
