using System.Buffers;
using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text;
using AxiosNet.Metadata;

namespace AxiosNet.Internal.Url;

/// <summary>baseURL + url 合并（combineURLs）与 params 序列化（buildURL）。</summary>
/// <remarks>序列化规则与 axios 默认 paramsSerializer 对齐：数组 <c>a[]=1&amp;a[]=2</c>、嵌套 <c>o[k]=v</c>、null 跳过、方括号不转义。</remarks>
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
        if (url is not null && string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(url, config, message: "url 不能为空白字符串：要请求 baseAddress 本身请显式传 \"/\"（或把 Url 置为 null）。");
        }

        var combined = Combine(baseAddress, url);

        // 去掉自定义序列化器返回串的前导 ?/&，并剥掉内部已格式化封套。
        string query;
        try
        {
            query = serializer is null
                ? ParamsSerializer.Serialize(queryParams, config)
                : NormalizeQuery(serializer(UnwrapFormattedValues(queryParams)));
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException and not AxiosAotMetadataException)
        {
            // 归类为「请求构造失败」；核心直调的对外码仍是 ERR_BAD_OPTION_VALUE。
            throw serializer is null
                ? AxiosException.RequestConstructionFailed(config, ex)
                : AxiosException.InvalidOption($"paramsSerializer 抛出了异常，查询串无法生成：{ex.Message}", config, ex);
        }

        // 查询串必须拼在 fragment 之前。
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
            pathAndQuery = SpanStringConcat.Concat(query.AsSpan(), fragment);
        }
        else if (target[^1] is '?' or '&')
        {
            // url 已以 ? / & 结尾时不再补分隔符。
            pathAndQuery = SpanStringConcat.Concat(target, query.AsSpan(), fragment);
        }
        else
        {
            pathAndQuery = target.Contains('?')
                ? SpanStringConcat.Concat(target, "&".AsSpan(), query.AsSpan(), fragment)
                : SpanStringConcat.Concat(target, "?".AsSpan(), query.AsSpan(), fragment);
        }

        if (UriCache.TryGetValue(pathAndQuery, out var cached))
        {
            return cached;
        }

        try
        {
            var uri = new Uri(pathAndQuery, UriKind.RelativeOrAbsolute);

            // 长度闸门：.NET 10 起 Uri 取消了长度上限，超限统一按 ERR_INVALID_URL 拒绝。
            if (pathAndQuery.Length > MaxUrlLength)
            {
                throw AxiosException.InvalidUrl(pathAndQuery, config);
            }

            // 只接受 http/https 绝对地址；IsFile 拦下 Unix 下被解析成 file:/// 的根相对路径。
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

    /// <summary>URL 长度上限（65519 字符），超过抛 <c>ERR_INVALID_URL</c>；.NET 10 起 <see cref="Uri"/> 不再限制。</summary>
    private const int MaxUrlLength = 65519;

    /// <summary>URI 缓存条目上限（256），满了整表清空。</summary>
    private const int UriCacheLimit = 256;

    /// <summary>已校验的绝对地址缓存（<see cref="Uri"/> 不可变，可安全共享）。</summary>
    private static readonly ConcurrentDictionary<string, Uri> UriCache = new(StringComparer.Ordinal);

    /// <summary>缓存条目计数（<c>Count</c> 要遍历全部桶，未命中路径不可用）。</summary>
    private static int _uriCacheCount;

    /// <summary>缓存已校验的绝对地址；达到上限时整表清空。</summary>
    /// <remarks>软上限：并发下可能短暂略超 256 条。</remarks>
    private static void CacheUri(string key, Uri uri)
    {
        if (Volatile.Read(ref _uriCacheCount) >= UriCacheLimit)
        {
            UriCache.Clear();
            Volatile.Write(ref _uriCacheCount, 0);
        }

        // 只有真的插进去才计数。
        if (UriCache.TryAdd(key, uri))
        {
            Interlocked.Increment(ref _uriCacheCount);
        }
    }

    /// <summary>axios 的路径合并（base 当作目录前缀）；base 的 query / fragment 按 RFC 3986 §5.2.2 解析。</summary>
    /// <remarks>非空相对路径不继承 base 的 query；base 的 fragment 永不继承。</remarks>
    private static string Combine(string? baseAddress, string? url)
    {
        // url 为 null 表示请求 baseAddress 本身：原样返回，不能裁掉尾斜杠。
        if (string.IsNullOrWhiteSpace(url))
        {
            return baseAddress!;
        }

        // 协议相对 URL（//host/path）：有 baseAddress 时补全 scheme。
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var separator = baseAddress?.IndexOf("://", StringComparison.Ordinal) ?? -1;
            return separator > 0
                ? SpanStringConcat.Concat(baseAddress.AsSpan(0, separator), ":".AsSpan(), url.AsSpan())
                : url;
        }

        if (string.IsNullOrWhiteSpace(baseAddress) || IsAbsoluteUrl(url))
        {
            return url;
        }

        // base 不是合法 http(s) 绝对地址时保留朴素拼接，由 Build 报 ERR_INVALID_URL。
        if (!TryGetBaseComponents(baseAddress, out var baseUri))
        {
            return SpanStringConcat.Concat(baseAddress.AsSpan().TrimEnd('/'), "/".AsSpan(), url.AsSpan().TrimStart('/'));
        }

        SplitReference(url, out var relativePath, out var relativeQuery, out var fragment);

        // 空路径引用沿用 base 的路径；非空相对路径不继承 base 的 query。
        if (relativePath.Length == 0)
        {
            var query = relativeQuery ?? baseUri.Query;
            var tail = query is null ? fragment : SpanStringConcat.Concat("?".AsSpan(), query.AsSpan(), fragment.AsSpan());
            return SpanStringConcat.Concat(baseUri.Root.AsSpan(), baseUri.Path.AsSpan(), tail.AsSpan());
        }

        var joinedPath = JoinPaths(baseUri.Path, relativePath);
        var relativeTail = relativeQuery is null
            ? fragment
            : SpanStringConcat.Concat("?".AsSpan(), relativeQuery.AsSpan(), fragment.AsSpan());
        return SpanStringConcat.Concat(baseUri.Root.AsSpan(), joinedPath.AsSpan(), relativeTail.AsSpan());
    }

    /// <summary>base 的组件拆分结果（已按 URI 规则解码，可安全再拼接）。</summary>
    /// <param name="Root">authority 部分（含 scheme，如 <c>https://host:8443</c>）。</param>
    /// <param name="Path">转义过的绝对路径（末尾斜杠由 <see cref="JoinPaths"/> 折叠）。</param>
    /// <param name="Query">原始查询串（不含 <c>?</c>），可能为 <c>null</c>。</param>
    private readonly record struct BaseComponents(string Root, string Path, string? Query);

    private static readonly ConcurrentDictionary<string, BaseComponents> BaseComponentCache = new(StringComparer.Ordinal);

    private static int _baseComponentCount;

    /// <summary>把 baseAddress 拆成 authority / path / query（fragment 丢弃），按字符串缓存。</summary>
    private static bool TryGetBaseComponents(string baseAddress, out BaseComponents components)
    {
        if (BaseComponentCache.TryGetValue(baseAddress, out components))
        {
            return true;
        }

        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) || uri.IsFile || !IsHttpScheme(uri))
        {
            return false;
        }

        components = new BaseComponents(
            uri.GetLeftPart(UriPartial.Authority),
            uri.AbsolutePath,
            uri.Query.Length > 0 ? uri.Query.Substring(1) : null);

        if (Volatile.Read(ref _baseComponentCount) >= UriCacheLimit)
        {
            BaseComponentCache.Clear();
            Volatile.Write(ref _baseComponentCount, 0);
        }

        if (BaseComponentCache.TryAdd(baseAddress, components))
        {
            Interlocked.Increment(ref _baseComponentCount);
        }

        return true;
    }

    /// <summary>按 axios 的斜杠折叠规则拼接两段路径：base 末尾与相对路径开头的 '/' 各收掉，再补一个分隔符。</summary>
    private static string JoinPaths(string basePath, string relativePath)
    {
        var trimmedBase = basePath.AsSpan().TrimEnd('/');
        var trimmedRelative = relativePath.AsSpan().TrimStart('/');

        if (trimmedBase.Length == 0)
        {
            // base 只有根斜杠：结果就是 / + 相对路径。
            return relativePath.StartsWith('/')
                ? SpanStringConcat.Concat("/".AsSpan(), trimmedRelative)
                : SpanStringConcat.Concat("/".AsSpan(), relativePath.AsSpan());
        }

        return trimmedRelative.Length == 0
            ? SpanStringConcat.Concat(trimmedBase, "/".AsSpan())
            : SpanStringConcat.Concat(trimmedBase, "/".AsSpan(), trimmedRelative);
    }

    /// <summary>把相对引用拆成 path / query / fragment；query 为 null 表示引用里没有 '?'，fragment 保留前导 '#'。</summary>
    private static void SplitReference(string reference, out string path, out string? query, out string fragment)
    {
        var hashIndex = reference.IndexOf('#');
        var end = hashIndex >= 0 ? hashIndex : reference.Length;
        fragment = hashIndex >= 0 ? reference.Substring(hashIndex) : string.Empty;

        var questionIndex = reference.IndexOf('?');
        if (questionIndex >= 0 && questionIndex < end)
        {
            path = reference.Substring(0, questionIndex);
            query = reference.Substring(questionIndex + 1, end - questionIndex - 1);
        }
        else
        {
            path = reference.Substring(0, end);
            query = null;
        }
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

    // 只认 ASCII 字母。
    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>只有 http/https 能交给传输层，其余在构造阶段就拒绝。</summary>
    private static bool IsHttpScheme(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    // 去首尾空白与前导 ? / &，不改顺序。
    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return query.Trim().TrimStart('?', '&');
    }

    /// <summary>剥掉内部 <see cref="PreformattedValue"/> 封套：自定义序列化器只应看到公开形态。</summary>
    /// <remarks>容器只在含标记时复制，否则保持原引用。</remarks>
    private static object? UnwrapFormattedValues(object? value)
    {
        switch (value)
        {
            case PreformattedValue formatted:
                return formatted.Text;

            case Dictionary<string, object?> map:
            {
                // 第一次遇到标记时才浅拷贝整张表。
                Dictionary<string, object?>? copy = null;
                foreach (var entry in map)
                {
                    var unwrapped = UnwrapFormattedValues(entry.Value);
                    if (copy is null)
                    {
                        if (ReferenceEquals(unwrapped, entry.Value))
                        {
                            continue;
                        }

                        copy = new Dictionary<string, object?>(map.Count);
                        foreach (var original in map)
                        {
                            copy[original.Key] = original.Value;
                        }
                    }

                    copy[entry.Key] = unwrapped;
                }

                return copy ?? value;
            }

            case List<object?> list:
            {
                List<object?>? copy = null;
                for (var index = 0; index < list.Count; index++)
                {
                    var unwrapped = UnwrapFormattedValues(list[index]);
                    if (copy is null)
                    {
                        if (ReferenceEquals(unwrapped, list[index]))
                        {
                            continue;
                        }

                        copy = new List<object?>(list);
                    }

                    copy[index] = unwrapped;
                }

                return copy ?? value;
            }

            default:
                return value;
        }
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

        // 已经是查询串。
        if (paramsObj is string rawQuery)
        {
            return UrlBuilder.NormalizeQuery(rawQuery);
        }

        var path = new ObjectPath();
        try
        {
            var pairs = new QueryPairs();
            try
            {
                var format = config?.SerializationOrNull?.CollectionFormat ?? AxiosCollectionFormat.Brackets;

                // 实例级钩子要下传到嵌套叶子。
                var formatter = config?.SerializationOrNull?.ValueText;
                if (paramsObj is JsonElement rootElement)
                {
                    // JsonElement 按 JSON 树展开，不走反射。
                    if (rootElement.ValueKind is JsonValueKind.String or JsonValueKind.Number
                        or JsonValueKind.True or JsonValueKind.False)
                    {
                        throw InvalidParamsRoot(paramsObj, config);
                    }

                    AppendJsonElement(null, rootElement, ref pairs, format, formatter);
                }
                else if (paramsObj is IDictionary<string, object> stringKeyedDictionary)
                {
                    // ExpandoObject（dynamic）等仅实现泛型字典的类型
                    foreach (var entry in stringKeyedDictionary)
                    {
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, ref path, format, memberFormat: null, formatter);
                    }
                }
                else if (paramsObj is IReadOnlyDictionary<string, object> readOnlyDictionary)
                {
                    // 只实现 IReadOnlyDictionary 的字典：按键值对展开。
                    foreach (var entry in readOnlyDictionary)
                    {
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, ref path, format, memberFormat: null, formatter);
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

                        Append(EscapeKey(entry.Key.ToString()!), entry.Value, ref pairs, ref path, format, memberFormat: null, formatter);
                    }
                }
                else if (QueryMap.CanEnumerate(paramsObj))
                {
                    // 只实现 IDictionary<string,T> 的自定义字典：按键值对展开。
                    foreach (var entry in QueryMap.Enumerate(paramsObj))
                    {
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, ref path, format, memberFormat: null, formatter);
                    }
                }
                else if (paramsObj is not byte[] && paramsObj is IEnumerable items)
                {
                    // 根集合用下标作键。
                    var index = 0;
                    foreach (var item in items)
                    {
                        Append("[" + index + "]", item, ref pairs, ref path, format, memberFormat: null, formatter);
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
                // 根对象：形状决定成员顺序与键名；query 展平不跳过 [JsonIgnore]。
                    var shape = ShapeCache.Get(paramsObj.GetType());
                    foreach (var member in shape.Members)
                    {
                        Append(member.EscapedName, member.GetValue(paramsObj), ref pairs, ref path, format, member.Format, formatter);
                    }
                }

                return pairs.Build();
            }
            finally
            {
                pairs.Dispose();
            }
        }
        catch (DataCycleException ex)
        {
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
        finally
        {
            path.Dispose();
        }
    }

    /// <summary>非法根类型：统一成 <c>ERR_BAD_OPTION_VALUE</c>，并说明可用的形态。</summary>
    private static AxiosException InvalidParamsRoot(object paramsObj, AxiosConfig? config)
        => AxiosException.InvalidOption(
            $"params 的类型 '{paramsObj.GetType()}' 无法序列化为查询串：请传对象 / 字典 / 集合，"
            + "或已经序列化好的查询串（string，如 \"a=1&b=2\"）。", config);

    /// <summary>把单个值按实际形态展平成查询键值对：null 跳过。</summary>
    /// <param name="prefix">已拼好的键名前缀。</param>
    /// <param name="value">待展平的值。</param>
    /// <param name="pairs">收集结果的键值对集合。</param>
    /// <param name="path">展平路径（递归与判环用）。</param>
    /// <param name="format">集合参数的分隔风格。</param>
    /// <param name="memberFormat">成员级格式化：只作用于标量叶子，集合逐元素继承。</param>
    /// <param name="formatter">实例级格式化钩子：成员声明缺失时生效。</param>
    private static void Append(
        string prefix,
        object? value,
        ref QueryPairs pairs,
        ref ObjectPath path,
        AxiosCollectionFormat format,
        ValueFormat? memberFormat,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        // 与 axios 一致：null 跳过。
        if (value is null)
        {
            return;
        }

        // 调用方已定型的文本：直接写入，不再套格式化。
        if (value is PreformattedValue formatted)
        {
            pairs.Add(prefix, EscapeValue(formatted.Text));
            return;
        }

        // 二进制叶子按 base64 输出，必须在 IEnumerable 分支之前判。
        if (ScalarTypes.IsBinaryType(value.GetType()))
        {
            pairs.Add(
                prefix,
                EscapeValue(ValueText.Format(value, ValueTextChannel.Query, memberFormat, formatter)!));
            return;
        }

        if (value is JsonElement element)
        {
            AppendJsonElement(prefix, element, ref pairs, format, formatter);
            return;
        }

        if (value is IDictionary<string, object> stringKeyedNested)
        {
            // 嵌套 ExpandoObject：prefix[key]=value
            path.Enter(stringKeyedNested);
            try
            {
                foreach (var entry in stringKeyedNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, ref path, format, memberFormat, formatter);
                }
            }
            finally
            {
                path.Exit(stringKeyedNested);
            }

            return;
        }

        // 只实现 IReadOnlyDictionary 的字典：按键值对展开。
        if (value is IReadOnlyDictionary<string, object> readOnlyNested)
        {
            path.Enter(readOnlyNested);
            try
            {
                foreach (var entry in readOnlyNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, ref path, format, memberFormat, formatter);
                }
            }
            finally
            {
                path.Exit(readOnlyNested);
            }

            return;
        }

        if (value is IDictionary dict)
        {
            path.Enter(dict);
            try
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is null)
                    {
                        continue;
                    }

                    Append($"{prefix}[{EscapeKey(entry.Key.ToString()!)}]", entry.Value, ref pairs, ref path, format, memberFormat, formatter);
                }
            }
            finally
            {
                path.Exit(dict);
            }

            return;
        }

        // 只实现 IDictionary<string,T> 的自定义字典：按键值对展开。
        if (value is IEnumerable mapValue && value is not string && value is not byte[] && QueryMap.CanEnumerate(mapValue))
        {
            path.Enter(mapValue);
            try
            {
                foreach (var entry in QueryMap.Enumerate(mapValue))
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, ref path, format, memberFormat, formatter);
                }
            }
            finally
            {
                path.Exit(mapValue);
            }

            return;
        }

        if (value is not string && value is not byte[] && value is IEnumerable enumerable)
        {
            if (format != AxiosCollectionFormat.Brackets)
            {
                AppendCollection(prefix, enumerable, ref pairs, ref path, format, memberFormat, formatter);
                return;
            }

            path.Enter(value);
            try
            {
                foreach (var item in enumerable)
                {
                    Append($"{prefix}[]", item, ref pairs, ref path, format, memberFormat, formatter);
                }
            }
            finally
            {
                path.Exit(value);
            }

            return;
        }

        if (IsScalar(value))
        {
            pairs.Add(
                prefix,
                EscapeValue(ValueText.Format(value, ValueTextChannel.Query, memberFormat, formatter)!));
            return;
        }

        // 嵌套对象：prefix[prop]=value（不跳过 [JsonIgnore]）。
        path.Enter(value);
        try
        {
            var shape = ShapeCache.Get(value.GetType(), prefix);
            foreach (var member in shape.Members)
            {
                Append(prefix + member.BracketSuffix, member.GetValue(value), ref pairs, ref path, format, member.Format, formatter);
            }
        }
        finally
        {
            path.Exit(value);
        }
    }

    /// <summary>非默认集合形态：<see cref="AxiosCollectionFormat.Multi"/> 展开成重复键，其余分隔形态拼成一个值（元素含对象时回落到方括号形态）。</summary>
    private static void AppendCollection(
        string prefix,
        IEnumerable enumerable,
        ref QueryPairs pairs,
        ref ObjectPath path,
        AxiosCollectionFormat format,
        ValueFormat? memberFormat,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        path.Enter(enumerable);
        try
        {
            // 其余分隔形态需物化一次，先判全标量。
            if (format == AxiosCollectionFormat.Multi)
            {
                foreach (var item in enumerable)
                {
                    Append(prefix, item, ref pairs, ref path, format, memberFormat, formatter);
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
                        Append($"{prefix}[]", fallback, ref pairs, ref path, format, memberFormat, formatter);
                    }

                    return;
                }

                parts.Add(item is PreformattedValue formatted
                    ? formatted.Text
                    : ValueText.Format(item, ValueTextChannel.Query, memberFormat, formatter)!);
            }

            if (parts.Count > 0)
            {
                pairs.Add(prefix, EscapeValue(string.Join(Separator(format), parts)));
            }
        }
        finally
        {
            path.Exit(enumerable);
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
        ref QueryPairs pairs,
        AxiosCollectionFormat format,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonElement(prefix is null ? EscapeKey(property.Name) : prefix + "[" + EscapeKey(property.Name) + "]", property.Value, ref pairs, format, formatter);
                }

                break;

            case JsonValueKind.Array:
            {
                if (format != AxiosCollectionFormat.Brackets)
                {
                    AppendJsonArray(prefix, element, ref pairs, format, formatter);
                    break;
                }

                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElement(prefix is null ? "[" + index + "]" : prefix + "[]", item, ref pairs, format, formatter);
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
                    // 根标量没有可表达的键名，忽略。
                    break;
                }

                pairs.Add(prefix, EscapeValue(ValueText.JsonScalar(element, ValueTextChannel.Query, formatter)));
                break;

            default:
                // null / undefined：直接跳过。
                break;
        }
    }

    /// <summary>JSON 树的非默认集合形态：Multi 重复键，其余拼成一个值（含对象 / 数组时回落方括号形态）。</summary>
    private static void AppendJsonArray(
        string? prefix,
        JsonElement element,
        ref QueryPairs pairs,
        AxiosCollectionFormat format,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (prefix is not null)
        {
            if (format == AxiosCollectionFormat.Multi)
            {
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonElement(prefix, item, ref pairs, format, formatter);
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

                parts.Add(ValueText.JsonScalar(item, ValueTextChannel.Query, formatter));
            }

            if (parts is { Count: > 0 })
            {
                pairs.Add(prefix, EscapeValue(string.Join(Separator(format), parts)));
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
            AppendJsonElement(prefix is null ? "[" + index + "]" : prefix + "[]", item, ref pairs, format, formatter);
            index++;
        }
    }

    // 叶子值判定：二进制族、已定型文本与标量都按叶子处理。
    private static bool IsScalar(object value)
        => value is PreformattedValue || ScalarTypes.IsBinaryType(value.GetType()) || ScalarTypes.IsScalarType(value.GetType());

    /// <summary>键名转义：同 <c>encodeURIComponent</c>，但把 <c>%5B</c> / <c>%5D</c> 还原成方括号。</summary>
    internal static string EscapeKey(string key)
        => NeedsEscape(key) ? Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]") : key;

    /// <summary>键是否需要转义：字母、数字、<c>- _ .</c> 与方括号放行，其余交给 BCL。</summary>
    private static bool NeedsEscape(string key)
    {
        foreach (var c in key)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or '[' or ']')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // 值转义用标准 encodeURIComponent 规则。
    private static string EscapeValue(string value)
        => Uri.EscapeDataString(value);

    /// <summary>查询键值对的缓冲：数组租自 <see cref="ArrayPool{T}"/>，用完归还；拼串时只产出一份字符串。</summary>
    private struct QueryPairs : IDisposable
    {
        /// <summary>已租下的键值对数组；首次 <see cref="Add"/> 时才租。</summary>
        private QueryPair[]? _items;

        /// <summary>已收集的键值对数量。</summary>
        public int Count { get; private set; }

        /// <summary>追加一对（键与值都已转义）。</summary>
        public void Add(string key, string value)
        {
            var items = _items;
            if (items is null)
            {
                items = _items = ArrayPool<QueryPair>.Shared.Rent(8);
            }
            else if (Count == items.Length)
            {
                var bigger = ArrayPool<QueryPair>.Shared.Rent(items.Length * 2);
                Array.Copy(items, bigger, Count);
                ArrayPool<QueryPair>.Shared.Return(items, clearArray: true);
                _items = items = bigger;
            }

            items[Count++] = new QueryPair(key, value);
        }

        /// <summary>拼成 <c>k=v&amp;k=v</c>；没有键值对时返回空串。</summary>
        public readonly string Build()
        {
            if (Count == 0)
            {
                return string.Empty;
            }

            var items = _items!;
            var capacity = Count - 1;
            for (var i = 0; i < Count; i++)
            {
                capacity += items[i].Key.Length + 1 + items[i].Value.Length;
            }

            var chars = ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                var position = 0;
                for (var i = 0; i < Count; i++)
                {
                    if (i > 0)
                    {
                        chars[position++] = '&';
                    }

                    position = Write(chars, position, items[i].Key);
                    chars[position++] = '=';
                    position = Write(chars, position, items[i].Value);
                }

                return new string(chars, 0, position);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }

        /// <summary>把一段文本写进目标缓冲，返回写入后的位置。</summary>
        private static int Write(char[] destination, int position, string text)
        {
            text.CopyTo(0, destination, position, text.Length);
            return position + text.Length;
        }

        /// <summary>归还租下的数组并清空引用。</summary>
        public void Dispose()
        {
            var items = _items;
            if (items is not null)
            {
                _items = null;
                ArrayPool<QueryPair>.Shared.Return(items, clearArray: true);
            }
        }
    }

    /// <summary>查询串的一对键值；两个串都已转义。</summary>
    private readonly struct QueryPair
    {
        public QueryPair(string key, string value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>已转义的键（方括号保持原形）。</summary>
        public string Key { get; }

        /// <summary>已转义的值。</summary>
        public string Value { get; }
    }
}
