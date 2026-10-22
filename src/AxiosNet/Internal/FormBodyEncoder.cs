using System.Collections;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>application/x-www-form-urlencoded</c> 通道：把结构化数据展平为键值对并编码成正文。
/// <para>
/// 展平规则：嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组 <c>[i]</c>）、null 跳过、标量取原始文本。
/// </para>
/// <para>
/// 直接读对象结构、<b>不经过序列化器</b>：表单是独立编码通道，命名只看
/// <see cref="AxiosConfig.FormFieldNameSelector"/> 与 <c>[JsonPropertyName]</c>（见 <see cref="BodyFields"/>）。
/// </para>
/// </summary>
internal static class FormBodyEncoder
{
    public static HttpContent Create(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        var pairs = new List<KeyValuePair<string, string>>(8);
        try
        {
            CollectFormPairs(null, data, pairs, encoding, config.FormFieldNameSelector, visited: null);
        }
        catch (DataCycleException ex)
        {
            // 环检测：无界递归会栈溢出（不可捕获），转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }

        // 表单体按已转义的 ASCII 串写入，因此编码只影响转义映射（默认 UTF-8 百分号编码）。
        var body = string.Join('&', pairs.Select(pair => pair.Key + "=" + pair.Value));

        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        RequestContentType.Set(content, RequestContentType.Compose(mediaType, encoding, appendCharset));
        return content;
    }

    /// <summary>
    /// 把结构化数据展平为表单键值对；传入 <see cref="JsonElement"/> 时按树语义展平（<see cref="Flatten"/>）。
    /// </summary>
    private static void CollectFormPairs(
        string? prefix,
        object? value,
        List<KeyValuePair<string, string>> pairs,
        Encoding encoding,
        Func<string?, string, string>? nameSelector,
        HashSet<object>? visited)
    {
        switch (value)
        {
            case null:
                return;

            case JsonElement element:
                Flatten(prefix, element, pairs, encoding, nameSelector);
                return;

            case string text:
                AddPair(prefix, text, pairs, encoding);
                return;

            // urlencoded 没有二进制表示：用 base64。
            case byte[] bytes:
                AddPair(prefix, Convert.ToBase64String(bytes), pairs, encoding);
                return;

            case ReadOnlyMemory<byte> memory:
                AddPair(prefix, Convert.ToBase64String(memory.Span), pairs, encoding);
                return;

            case Memory<byte> memory:
                AddPair(prefix, Convert.ToBase64String(memory.Span), pairs, encoding);
                return;

            case ArraySegment<byte> segment:
                AddPair(prefix, Convert.ToBase64String(segment.AsSpan()), pairs, encoding);
                return;

            // 文件类内容在 urlencoded 里无法表达：multipart 才有「字段 + 文件名」的位置。
            case FormFile:
            case Stream:
            case HttpContent:
                throw new InvalidOperationException(
                    "application/x-www-form-urlencoded 无法承载文件部件：请改用 multipart/form-data。");

            case IDictionary<string, object?> genericDictionary:
            {
                // 含 ExpandoObject（dynamic）
                visited = ObjectGraph.Enter(visited, genericDictionary);
                try
                {
                    foreach (var entry in genericDictionary)
                    {
                        CollectFormPairs(
                            BodyFields.CombineName(nameSelector, prefix, entry.Key),
                            entry.Value,
                            pairs,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, genericDictionary);
                }

                return;
            }

            // 只实现 IReadOnlyDictionary 的字典：按键值对展开，否则会被当 POCO 反射出 Key/Value 字段。
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
            {
                visited = ObjectGraph.Enter(visited, readOnlyDictionary);
                try
                {
                    foreach (var entry in readOnlyDictionary)
                    {
                        CollectFormPairs(
                            BodyFields.CombineName(nameSelector, prefix, entry.Key),
                            entry.Value,
                            pairs,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, readOnlyDictionary);
                }

                return;
            }

            case IDictionary dictionary:
            {
                visited = ObjectGraph.Enter(visited, dictionary);
                try
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is null)
                        {
                            continue;
                        }

                        CollectFormPairs(
                            BodyFields.CombineName(nameSelector, prefix, entry.Key.ToString() ?? string.Empty),
                            entry.Value,
                            pairs,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, dictionary);
                }

                return;
            }
        }

        var type = value.GetType();
        if (BodyFields.IsScalar(type))
        {
            AddPair(prefix, BodyFields.ScalarToString(value), pairs, encoding);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            visited = ObjectGraph.Enter(visited, value);
            try
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    // 根数组没有键名，用下标保证可还原；嵌套数组沿用 a[] 形式。
                    var name = prefix is null ? "[" + index + "]" : prefix + "[]";
                    CollectFormPairs(name, item, pairs, encoding, nameSelector, visited);
                    index++;
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, value);
            }

            return;
        }

        visited = ObjectGraph.Enter(visited, value);
        try
        {
            foreach (var property in ReflectionCache.GetReadableProperties(type))
            {
                if (BodyFields.IsIgnored(property))
                {
                    continue;
                }

                CollectFormPairs(
                    BodyFields.CombineName(nameSelector, prefix, BodyFields.ResolveFieldName(property)),
                    property.GetValue(value),
                    pairs,
                    encoding,
                    nameSelector,
                    visited);
            }
        }
        finally
        {
            ObjectGraph.Exit(visited, value);
        }
    }

    /// <summary>加入一个键值对；根标量没有可表达的键名，直接忽略（不伪造 key）。</summary>
    private static void AddPair(string? prefix, string value, List<KeyValuePair<string, string>> pairs, Encoding encoding)
    {
        if (prefix is null)
        {
            return;
        }

        pairs.Add(new KeyValuePair<string, string>(EncodeKey(prefix, encoding), EncodeValue(value, encoding)));
    }

    /// <summary>
    /// 把 JSON 树展平为表单键值对：
    /// 嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组用 <c>[i]</c>）、
    /// null 跳过、标量取原始文本。
    /// </summary>
    private static void Flatten(
        string? prefix,
        JsonElement element,
        List<KeyValuePair<string, string>> pairs,
        Encoding encoding,
        Func<string?, string, string>? nameSelector)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(
                        BodyFields.CombineName(nameSelector, prefix, property.Name),
                        property.Value,
                        pairs,
                        encoding,
                        nameSelector);
                }

                break;

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    // 根数组没有键名，用下标保证可还原；嵌套数组沿用 a[] 形式。
                    Flatten(prefix is null ? "[" + index + "]" : prefix + "[]", item, pairs, encoding, nameSelector);
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
                    // 根标量在表单里没有可表达的键名，忽略（不伪造 key）。
                    break;
                }

                pairs.Add(new KeyValuePair<string, string>(
                    EncodeKey(prefix, encoding),
                    EncodeValue(BodyFields.ScalarText(element), encoding)));
                break;

            default:
                // null / undefined：与 axios 一致，直接跳过。
                break;
        }
    }

    /// <summary>键名保留 <c>[</c> / <c>]</c>（<c>a[b]</c> 形式可读且被主流服务端接受），其余同值编码。</summary>
    private static string EncodeKey(string value, Encoding encoding)
        => PercentEncode(value, encoding).Replace("%5B", "[").Replace("%5D", "]");

    private static string EncodeValue(string value, Encoding encoding) => PercentEncode(value, encoding);

    private static string PercentEncode(string value, Encoding encoding)
    {
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            // 与 FormUrlEncodedContent 完全一致：空格转义为 '+'。
            return Uri.EscapeDataString(value).Replace("%20", "+");
        }

        var bytes = encoding.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b == (byte)' ')
            {
                builder.Append('+');
            }
            else if (IsUnreserved(b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(HexUpper(b >> 4)).Append(HexUpper(b & 0xF));
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static char HexUpper(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);
}
