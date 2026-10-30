using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>application/x-www-form-urlencoded</c> 通道：把结构化数据展平为键值对并编码成正文
/// （嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>、根数组 <c>[i]</c>、null 跳过、标量取原始文本，不经过序列化器）。
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
            CollectFormPairs(null, data, pairs, encoding, config.SerializationOrNull?.FormFieldNameSelector, visited: null);
        }
        catch (DataCycleException ex)
        {
            // 环检测：转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
        catch (UnsupportedFormValueException ex)
        {
            // 表单装不下文件部件：归 ERR_BAD_OPTION_VALUE。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }

        // 正文是已转义的 ASCII 串，编码只影响转义映射。
        var body = BuildBody(pairs);

        var content = RequestCompression.WrapBytes(Encoding.ASCII.GetBytes(body), config);
        RequestContentType.Set(content, RequestContentType.Compose(mediaType, encoding, appendCharset), config);
        return content;
    }

    /// <summary>把键值对拼成表单正文；容量按精确长度预估，一次成型不重排。</summary>
    private static string BuildBody(List<KeyValuePair<string, string>> pairs)
    {
        if (pairs.Count == 0)
        {
            return string.Empty;
        }

        var capacity = (pairs.Count - 1) * 2;
        foreach (var pair in pairs)
        {
            capacity += pair.Key.Length + pair.Value.Length + 1;
        }

        var builder = new StringBuilder(capacity);
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(pairs[i].Key).Append('=').Append(pairs[i].Value);
        }

        return builder.ToString();
    }

    /// <summary>把结构化数据展平为表单键值对；<see cref="JsonElement"/> 按树语义展平（<see cref="Flatten"/>）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "表单值的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
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

            // 文件类内容在 urlencoded 里无法表达。
            case FormFile:
            case Stream:
            case HttpContent:
                throw new UnsupportedFormValueException("application/x-www-form-urlencoded 无法承载文件部件：请改用 multipart/form-data。");

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

            // 只实现 IReadOnlyDictionary 的字典：按键值对展开，不走 POCO 反射。
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

    /// <summary>把 JSON 树展平为表单键值对：嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组 <c>[i]</c>）、null 跳过。</summary>
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
                    Flatten(BodyFields.CombineName(nameSelector, prefix, property.Name), property.Value, pairs, encoding, nameSelector);
                }

                break;

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
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
                    break;
                }

                pairs.Add(new KeyValuePair<string, string>(EncodeKey(prefix, encoding), EncodeValue(BodyFields.ScalarText(element), encoding)));
                break;

            default:
                // null / undefined：跳过。
                break;
        }
    }

    /// <summary>键名保留 <c>[</c> / <c>]</c>，其余同值编码。</summary>
    private static string EncodeKey(string value, Encoding encoding)
        => PercentEncode(value, encoding, keepBrackets: true);

    private static string EncodeValue(string value, Encoding encoding) => PercentEncode(value, encoding, keepBrackets: false);

    /// <summary>
    /// 单趟百分号编码：按字节流直出可保留的 ASCII，其余转义（空格转 <c>+</c>），不在 EscapeDataString 之后再串 Replace。
    /// </summary>
    private static string PercentEncode(string value, Encoding encoding, bool keepBrackets)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var byteCount = encoding.GetByteCount(value);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            encoding.GetBytes(value.AsSpan(), rented);
            var bytes = rented.AsSpan(0, byteCount);

            // 容量一次算准：先数一遍需要转义的字节。
            var escaped = 0;
            foreach (var b in bytes)
            {
                if (b != (byte)' ' && !IsLiteral(b, keepBrackets))
                {
                    escaped++;
                }
            }

            var builder = new StringBuilder(bytes.Length + escaped * 2);
            foreach (var b in bytes)
            {
                if (b == (byte)' ')
                {
                    // 与 FormUrlEncodedContent 完全一致：空格转义为 '+'。
                    builder.Append('+');
                }
                else if (IsLiteral(b, keepBrackets))
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
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsLiteral(byte b, bool keepBrackets)
        => IsUnreserved(b) || (keepBrackets && b is (byte)'[' or (byte)']');

    private static bool IsUnreserved(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static char HexUpper(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);
}

/// <summary>
/// 表单通道装不下某个值（文件部件），由 <see cref="FormBodyEncoder"/> 转成 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
/// </summary>
internal sealed class UnsupportedFormValueException : Exception
{
    public UnsupportedFormValueException(string message)
        : base(message)
    {
    }
}
