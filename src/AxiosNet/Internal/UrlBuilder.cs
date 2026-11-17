using System.Buffers;
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
        if (url is not null && string.IsNullOrWhiteSpace(url))
        {
            throw AxiosException.InvalidUrl(url, config, message: "url 不能为空白字符串：要请求 baseAddress 本身请显式传 \"/\"（或把 Url 置为 null）。");
        }

        var combined = Combine(baseAddress, url);

        // 自定义序列化器返回的串允许带前导 ?/&，这里统一去掉；回调异常统一成 ERR_BAD_OPTION_VALUE。
        // 自定义序列化器只应看到「最终文本 / 原始值」，绑定层内部的已格式化封套在这里剥掉。
        string query;
        try
        {
            query = serializer is null
                ? ParamsSerializer.Serialize(queryParams, config)
                : NormalizeQuery(serializer(UnwrapFormattedValues(queryParams)));
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException and not AxiosAotMetadataException)
        {
            // 只有默认展平会触碰调用方的值（取值 / 格式化）。这里抛中性的「请求构造失败」分类：
            // 核心直调的对外错误码仍是 ERR_BAD_OPTION_VALUE；外层可据此改判自己的错误码（不感知分层概念）。
            throw serializer is null
                ? AxiosException.RequestConstructionFailed(config, ex)
                : AxiosException.InvalidOption($"paramsSerializer 抛出了异常，查询串无法生成：{ex.Message}", config, ex);
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
            pathAndQuery = SpanStringConcat.Concat(query.AsSpan(), fragment);
        }
        else if (target[^1] is '?' or '&')
        {
            // url 已以 ? / & 结尾时不能再补分隔符，否则会多出一个空参数。
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

            // 长度闸门：.NET 10 起 Uri 取消了约 65519 字符的上限，同一个超长地址会从「构造失败」变成一次真实请求。
            // 边界取 net6 / net8 的实测值，三个运行时统一按 ERR_INVALID_URL 拒绝。
            // 放在 new Uri 之后：旧目标上 Uri 先抛，既有的异常归一化路径不受影响。
            if (pathAndQuery.Length > MaxUrlLength)
            {
                throw AxiosException.InvalidUrl(pathAndQuery, config);
            }

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

    /// <summary>
    /// URL 长度上限，对应 <c>ERR_INVALID_URL</c>。取 .NET 10 之前 <see cref="Uri"/> 的实测边界
    /// （65519 字符可用、65520 起抛 <see cref="UriFormatException"/>）；.NET 10 取消了这道限制，
    /// 多目标运行时下必须由库自己守住同一个边界，否则同一份代码在不同运行时上接受不同的输入。
    /// </summary>
    private const int MaxUrlLength = 65519;

    /// <summary>解析结果缓存上限：满了整表清空，URL 集合频繁变化时不会无限增长。</summary>
    private const int UriCacheLimit = 256;

    /// <summary>已校验过的绝对地址缓存（<see cref="Uri"/> 不可变，可安全共享）。</summary>
    private static readonly ConcurrentDictionary<string, Uri> UriCache = new(StringComparer.Ordinal);

    /// <summary>缓存条目的近似计数：<see cref="ConcurrentDictionary{TKey,TValue}.Count"/> 要遍历全部桶并逐个加锁，未命中路径上不能用它。</summary>
    private static int _uriCacheCount;

    /// <summary>
    /// 缓存已校验的绝对地址；达到上限时整表清空（不做逐项淘汰，胜在简单且不会无界增长）。
    /// </summary>
    /// <remarks>上限是软上限：清表与并发写入交错时计数会短暂偏小，表现为略超 256 条，下一次清表即收敛。</remarks>
    private static void CacheUri(string key, Uri uri)
    {
        if (Volatile.Read(ref _uriCacheCount) >= UriCacheLimit)
        {
            UriCache.Clear();
            Volatile.Write(ref _uriCacheCount, 0);
        }

        // 并发下另一个线程可能刚插了同一个键：只有真的插进去才计数。
        if (UriCache.TryAdd(key, uri))
        {
            Interlocked.Increment(ref _uriCacheCount);
        }
    }

    /// <summary>
    /// 对应 axios 的路径合并（base 当作目录前缀），base 的 query / fragment 按 RFC 3986 §5.2.2 解析：
    /// 非空相对路径不继承 base 的 query，base 的 fragment 永不继承，引用自带的 query / fragment 保留。
    /// </summary>
    private static string Combine(string? baseAddress, string? url)
    {
        // url 为 null 表示「请求 baseAddress 本身」：原样返回，不能裁掉尾斜杠——
        // 否则 baseAddress 为 "https://h/api/" 时 null 得到 "/api"、显式传 "/" 得到 "/api/"，
        // 同一语义两种地址，路由与签名校验（含尾斜杠）都会对不上。
        if (string.IsNullOrWhiteSpace(url))
        {
            return baseAddress!;
        }

        // 协议相对 URL（//host/path）：有 baseAddress 时按它的 scheme 补全，否则原样返回交给 Build 报错。
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

        // base 必须是合法 http(s) 绝对地址才做组件解析；否则保留旧拼接，由 Build 统一报 ERR_INVALID_URL。
        if (!TryGetBaseComponents(baseAddress, out var baseUri))
        {
            return SpanStringConcat.Concat(baseAddress.AsSpan().TrimEnd('/'), "/".AsSpan(), url.AsSpan().TrimStart('/'));
        }

        SplitReference(url, out var relativePath, out var relativeQuery, out var fragment);

        // 空路径引用（"?q" / "#f"）：路径沿用 base；query 用引用自己的，引用没带才继承 base 的。
        // 非空相对路径：base 的 query 不继承（RFC 3986 §5.2.2），base 的 fragment 在任何情况下都不继承。
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

    /// <summary>base 的组件拆分结果（已按 URI 规则解码成可安全再拼接的文本）。</summary>
    /// <param name="Root">authority 部分（含 scheme，如 <c>https://host:8443</c>）。</param>
    /// <param name="Path">原始绝对路径（按 URI 规则转义过；末尾斜杠由 <see cref="JoinPaths"/> 折叠）。</param>
    /// <param name="Query">base 自带的查询串（不含前导 <c>?</c>）；没有时为 <c>null</c>。</param>
    private readonly record struct BaseComponents(string Root, string Path, string? Query);

    private static readonly ConcurrentDictionary<string, BaseComponents> BaseComponentCache = new(StringComparer.Ordinal);

    private static int _baseComponentCount;

    /// <summary>
    /// 把 baseAddress 拆成 authority / path / query（fragment 直接丢弃）：用 <see cref="Uri"/> 解析而不是找字符，
    /// 才能正确处理 userinfo、IPv6 与转义。按字符串缓存，避免每次请求都重新解析同一个 BaseAddress。
    /// </summary>
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
            // base 只有根斜杠：结果就是 "/" + 相对路径（相对路径本身以 '/' 开头时不再重复补）。
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

    // ASCII 字母判定：解析 scheme 前缀时只认 ASCII，别把 Unicode 字母放进来。
    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>只有 http/https 能交给传输层，其余（ftp/data/ws…）在构造阶段就拒绝。</summary>
    private static bool IsHttpScheme(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    // 查询串规整：去首尾空白与前导 ? / &，不改顺序（顺序会影响部分服务端的缓存键）。
    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return query.Trim().TrimStart('?', '&');
    }

    /// <summary>
    /// 自定义 ParamsSerializer 只应看到公开形态：调用方可用 <see cref="PreformattedValue"/> 标记「已定型文本」，
    /// 这是内部约定，不能漏进用户回调（否则常见写法 <c>(string)value</c> 会 InvalidCast）。
    /// 容器只在真的含标记时复制，普通调用（无标记）保持原引用。
    /// </summary>
    private static object? UnwrapFormattedValues(object? value)
    {
        switch (value)
        {
            case PreformattedValue formatted:
                return formatted.Text;

            case Dictionary<string, object?> map:
            {
                // 先按「假设一个都不需要替换」遍历；第一次真的遇到标记时才把整张表浅拷贝一份，
                // 其余项直接从原表抄过去（顺序无关，拷贝只发生一次）。
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

        // 已经是查询串（`"a=1&b=2"`）：原样透传，不走反射。
        if (paramsObj is string rawQuery)
        {
            return UrlBuilder.NormalizeQuery(rawQuery);
        }

        try
        {
            var pairs = new QueryPairs();
            try
            {
                var format = config?.SerializationOrNull?.CollectionFormat ?? AxiosCollectionFormat.Brackets;

                // 实例级钩子：嵌套对象的叶子也要用同一份钩子，否则「查询里能格式化」只对顶层成立。
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
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, visited: null, format, memberFormat: null, formatter);
                    }
                }
                else if (paramsObj is IReadOnlyDictionary<string, object> readOnlyDictionary)
                {
                    // 只实现 IReadOnlyDictionary 的字典：按键值对展开，不走 POCO 反射。
                    foreach (var entry in readOnlyDictionary)
                    {
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, visited: null, format, memberFormat: null, formatter);
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

                        Append(EscapeKey(entry.Key.ToString()!), entry.Value, ref pairs, visited: null, format, memberFormat: null, formatter);
                    }
                }
                else if (QueryMap.CanEnumerate(paramsObj))
                {
                    // 只实现 IDictionary<string,T> 的自定义字典（T 不是 object）或值类型字典：按键值对展开。
                    foreach (var entry in QueryMap.Enumerate(paramsObj))
                    {
                        Append(EscapeKey(entry.Key), entry.Value, ref pairs, visited: null, format, memberFormat: null, formatter);
                    }
                }
                else if (paramsObj is not byte[] && paramsObj is IEnumerable items)
                {
                    // 根集合用下标作键（与表单展平一致）。
                    var index = 0;
                    foreach (var item in items)
                    {
                        Append("[" + index + "]", item, ref pairs, visited: null, format, memberFormat: null, formatter);
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
                    // 成员级格式化来自形状自身（与嵌套对象同一条规则），否则根对象上的 [Format] 会被静默忽略。
                    var shape = ShapeCache.Get(paramsObj.GetType());
                    foreach (var member in shape.Members)
                    {
                        Append(member.EscapedName, member.GetValue(paramsObj), ref pairs, visited: null, format, member.Format, formatter);
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
            // 环检测：转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
    }

    /// <summary>非法根类型：统一成 <c>ERR_BAD_OPTION_VALUE</c>，并说明可用的形态。</summary>
    private static AxiosException InvalidParamsRoot(object paramsObj, AxiosConfig? config)
        => AxiosException.InvalidOption(
            $"params 的类型 '{paramsObj.GetType()}' 无法序列化为查询串：请传对象 / 字典 / 集合，"
            + "或已经序列化好的查询串（string，如 \"a=1&b=2\"）。", config);

    /// <summary>把单个值按实际形态展平成查询键值对：null 跳过，字典 / 集合 / JSON 树 / 标量 / 嵌套对象分别处理。</summary>
    /// <param name="prefix">已转义的键前缀。</param>
    /// <param name="value">待展平的值。</param>
    /// <param name="pairs">输出键值对。</param>
    /// <param name="visited">环检测栈。</param>
    /// <param name="format">集合编码形态。</param>
    /// <param name="memberFormat">当前成员级格式化：只作用于标量叶子，集合逐元素继承，嵌套对象改用成员自己的声明。</param>
    /// <param name="formatter">实例级格式化钩子：标量叶子在成员声明缺失时生效（嵌套对象同样下传）。</param>
    private static void Append(
        string prefix,
        object? value,
        ref QueryPairs pairs,
        HashSet<object>? visited,
        AxiosCollectionFormat format,
        ValueFormat? memberFormat,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        // 与 axios 一致：null 直接跳过
        if (value is null)
        {
            return;
        }

        // 调用方已定型的文本：直接写入，不能再让格式化钩子对「文本」套第二遍。
        if (value is PreformattedValue formatted)
        {
            pairs.Add(prefix, EscapeValue(formatted.Text));
            return;
        }

        // 二进制族的叶子语义：查询串里取 base64，与表单 / ValueText 同口径。
        // 必须在 IEnumerable 分支之前：ArraySegment<byte> 同时实现 IEnumerable；Memory 族则不能被当作嵌套对象反射。
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
            // 嵌套 ExpandoObject（dynamic）：prefix[key]=value
            visited = ObjectGraph.Enter(visited, stringKeyedNested);
            try
            {
                foreach (var entry in stringKeyedNested)
                {
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, visited, format, memberFormat, formatter);
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
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, visited, format, memberFormat, formatter);
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

                    Append($"{prefix}[{EscapeKey(entry.Key.ToString()!)}]", entry.Value, ref pairs, visited, format, memberFormat, formatter);
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
                    Append($"{prefix}[{EscapeKey(entry.Key)}]", entry.Value, ref pairs, visited, format, memberFormat, formatter);
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
                AppendCollection(prefix, enumerable, ref pairs, visited, format, memberFormat, formatter);
                return;
            }

            visited = ObjectGraph.Enter(visited, value);
            try
            {
                foreach (var item in enumerable)
                {
                    Append($"{prefix}[]", item, ref pairs, visited, format, memberFormat, formatter);
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
            pairs.Add(
                prefix,
                EscapeValue(ValueText.Format(value, ValueTextChannel.Query, memberFormat, formatter)!));
            return;
        }

        // 嵌套对象（匿名类型 / record / POCO）：形状决定成员，序列化为 prefix[prop]=value（不跳过 [JsonIgnore]）。
        visited = ObjectGraph.Enter(visited, value);
        try
        {
            var shape = ShapeCache.Get(value.GetType(), prefix);
            foreach (var member in shape.Members)
            {
                // 后缀（含转义）在形状里预计算，这里只需一次前缀拼接；成员级格式化来自形状自身。
                Append(prefix + member.BracketSuffix, member.GetValue(value), ref pairs, visited, format, member.Format, formatter);
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
        ref QueryPairs pairs,
        HashSet<object>? visited,
        AxiosCollectionFormat format,
        ValueFormat? memberFormat,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        visited = ObjectGraph.Enter(visited, enumerable);
        try
        {
            // Multi 直接流式枚举；其余分隔形态需物化一次（先判全标量，再拼接或回落）。
            if (format == AxiosCollectionFormat.Multi)
            {
                foreach (var item in enumerable)
                {
                    Append(prefix, item, ref pairs, visited, format, memberFormat, formatter);
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
                        Append($"{prefix}[]", fallback, ref pairs, visited, format, memberFormat, formatter);
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
                    // 根标量没有可表达的键名，忽略而不是伪造一个 key。
                    break;
                }

                pairs.Add(prefix, EscapeValue(ValueText.JsonScalar(element, ValueTextChannel.Query, formatter)));
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

    // 叶子值判定：二进制族按 base64 当叶子、已定型文本当叶子，其余标量直接 ToString 拼进去。
    private static bool IsScalar(object value)
        // 二进制族（byte[] / Memory / ReadOnlyMemory / ArraySegment）都算「叶子值」：query 里按 base64 输出（可逆），不再往里展开。
        // PreformattedValue 是已定型的文本，同样按叶子处理（否则会被集合逻辑回落到方括号形态）。
        => value is PreformattedValue || ScalarTypes.IsBinaryType(value.GetType()) || ScalarTypes.IsScalarType(value.GetType());

    /// <summary>
    /// 键名转义：同 axios 默认 encoder（<c>encodeURIComponent</c>），但把 <c>%5B</c> / <c>%5D</c> 还原成 <c>[</c> <c>]</c>，
    /// 保持 <c>a[b]=v</c> 的可读形态。query 展平与形状预计算共用这一个入口。
    /// </summary>
    internal static string EscapeKey(string key)
        => NeedsEscape(key) ? Uri.EscapeDataString(key).Replace("%5B", "[").Replace("%5D", "]") : key;

    /// <summary>
    /// 键是否需要转义：只有「字母 / 数字 / <c>-</c> <c>_</c> <c>.</c> / 方括号」直接放行，
    /// 其余一律交给 BCL（免转义集合刻意取得比 <c>Uri.EscapeDataString</c> 更窄）。
    /// </summary>
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

    // 值转义用标准 encodeURIComponent 规则（键那边会额外还原方括号）。
    private static string EscapeValue(string value)
        => Uri.EscapeDataString(value);

    /// <summary>
    /// 查询键值对的缓冲：数组租自 <see cref="ArrayPool{T}"/>（稳态不分配），按需翻倍，用完归还；
    /// 拼成查询串时先算准容量，再逐段写进池化字符缓冲，只产出一份字符串。
    /// </summary>
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

        /// <summary>归还租下的数组并清空引用，避免池里长期持有业务数据。</summary>
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

        /// <summary>已转义的键（其中的方括号保持原形）。</summary>
        public string Key { get; }

        /// <summary>已转义的值。</summary>
        public string Value { get; }
    }
}
