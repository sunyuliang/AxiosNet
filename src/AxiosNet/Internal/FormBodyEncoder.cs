using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AxiosNet.Metadata;

namespace AxiosNet.Internal;

/// <summary>
/// <c>application/x-www-form-urlencoded</c> 通道：把结构化数据展平为键值对并编码成正文
/// （嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>、根数组 <c>[i]</c>、null 跳过、标量取原始文本，不经过序列化器）。
/// </summary>
internal static class FormBodyEncoder
{
    /// <summary>展平数据、百分号编码成正文，并按需压缩 / 写 Content-Type。</summary>
    public static HttpContent Create(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        var pairs = new List<KeyValuePair<string, string>>(8);
        var options = new FormFlattenOptions(encoding, config.SerializationOrNull?.FormFieldNameSelector, config.ValueFormatter);
        try
        {
            CollectFormPairs(null, data, pairs, options, visited: null, memberFormat: null);
        }
        catch (DataCycleException ex)
        {
            // 正文不再产出：数据里的流 / FormFile 按 FormFile 契约（异常时 Dispose）一并回收。
            MultipartOwnership.DisposeOwned(data);

            // 环检测：转成带配置的选项错误。
            throw AxiosException.InvalidOption(ex.Message, config, ex);
        }
        catch (UnsupportedFormValueException ex)
        {
            // 表单装不下文件部件：归 ERR_BAD_OPTION_VALUE，流同样不能留到 GC。
            MultipartOwnership.DisposeOwned(data);
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
    private static void CollectFormPairs(
        string? prefix,
        object? value,
        List<KeyValuePair<string, string>> pairs,
        FormFlattenOptions options,
        HashSet<object>? visited,
        ValueFormat? memberFormat)
    {
        switch (value)
        {
            case null:
                return;

            case JsonElement element:
                Flatten(prefix, element, pairs, options);
                return;

            case string text:
                AddPair(prefix, ValueText.Format(text, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
                return;

            // urlencoded 没有二进制表示：用 base64（与查询串同一条规则，见 ValueText）。
            case byte[] bytes:
                AddPair(prefix, ValueText.Format(bytes, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
                return;

            case ReadOnlyMemory<byte> memory:
                AddPair(prefix, ValueText.Format(memory, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
                return;

            case Memory<byte> memory:
                AddPair(prefix, ValueText.Format(memory, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
                return;

            case ArraySegment<byte> segment:
                AddPair(prefix, ValueText.Format(segment, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
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
                            BodyFields.CombineName(options.NameSelector, prefix, entry.Key),
                            entry.Value,
                            pairs,
                            options,
                            visited,
                            memberFormat);
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
                            BodyFields.CombineName(options.NameSelector, prefix, entry.Key),
                            entry.Value,
                            pairs,
                            options,
                            visited,
                            memberFormat);
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
                            BodyFields.CombineName(options.NameSelector, prefix, entry.Key.ToString() ?? string.Empty),
                            entry.Value,
                            pairs,
                            options,
                            visited,
                            memberFormat);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, dictionary);
                }

                return;
            }

            // 只实现 IDictionary<string,T> 的自定义字典（T 不是 object）或值类型字典：按键值对展开。
            case IEnumerable map when QueryMap.CanEnumerate(map):
            {
                visited = ObjectGraph.Enter(visited, map);
                try
                {
                    foreach (var entry in QueryMap.Enumerate(map))
                    {
                        CollectFormPairs(
                            BodyFields.CombineName(options.NameSelector, prefix, entry.Key),
                            entry.Value,
                            pairs,
                            options,
                            visited,
                            memberFormat);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, map);
                }

                return;
            }
        }

        var type = value.GetType();
        if (ScalarTypes.IsScalarType(type))
        {
            AddPair(prefix, ValueText.Format(value, ValueTextChannel.Form, memberFormat, options.Formatter)!, pairs, options.Encoding);
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
                    CollectFormPairs(name, item, pairs, options, visited, memberFormat);
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
            var shape = ShapeCache.Get(type, prefix);
            foreach (var member in shape.Members)
            {
                if (member.Ignored)
                {
                    continue;
                }

                CollectFormPairs(
                    BodyFields.CombineName(options.NameSelector, prefix, member.FieldName),
                    member.GetValue(value),
                    pairs,
                    options,
                    visited,
                    member.Format);
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
        FormFlattenOptions options)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(BodyFields.CombineName(options.NameSelector, prefix, property.Name), property.Value, pairs, options);
                }

                break;

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(prefix is null ? "[" + index + "]" : prefix + "[]", item, pairs, options);
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

                pairs.Add(new KeyValuePair<string, string>(EncodeKey(prefix, options.Encoding), EncodeValue(ValueText.JsonScalar(element, ValueTextChannel.Form, options.Formatter), options.Encoding)));
                break;

            default:
                // null / undefined：跳过。
                break;
        }
    }

    /// <summary>键名保留 <c>[</c> / <c>]</c>，其余同值编码。</summary>
    private static string EncodeKey(string value, Encoding encoding)
        => PercentEncode(value, encoding, keepBrackets: true);

    /// <summary>字段值编码：方括号也转义（值上没有数组语义）。</summary>
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

    /// <summary>字节可否原样输出：非保留字符总可以，字段名额外保留方括号。</summary>
    private static bool IsLiteral(byte b, bool keepBrackets)
        => IsUnreserved(b) || (keepBrackets && b is (byte)'[' or (byte)']');

    /// <summary>RFC 3986 的 unreserved 集合（字母、数字、<c>-</c> <c>.</c> <c>_</c> <c>~</c>），其余一律百分号转义。</summary>
    private static bool IsUnreserved(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    /// <summary>单个半字节转大写十六进制字符（与 <c>FormUrlEncodedContent</c> 的输出一致）。</summary>
    private static char HexUpper(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);
}

/// <summary>表单展平的共享参数：编码、字段名选择器与实例级格式化钩子。</summary>
internal readonly struct FormFlattenOptions
{
    public FormFlattenOptions(Encoding encoding, Func<string?, string, string>? nameSelector, Func<object?, ValueTextChannel, string?>? formatter)
    {
        Encoding = encoding;
        NameSelector = nameSelector;
        Formatter = formatter;
    }

    /// <summary>百分号编码使用的字符编码。</summary>
    public Encoding Encoding { get; }

    /// <summary>字段名生成器（<see cref="AxiosConfig.SerializationSettings.FormFieldNameSelector"/>）。</summary>
    public Func<string?, string, string>? NameSelector { get; }

    /// <summary>实例级格式化钩子（<c>AxiosConfig.ValueFormatter</c>）。</summary>
    public Func<object?, ValueTextChannel, string?>? Formatter { get; }
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
