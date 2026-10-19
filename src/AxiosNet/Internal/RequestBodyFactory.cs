using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 把 <see cref="AxiosConfig.Data"/> 转成 <see cref="HttpContent"/>。
/// <para>
/// 唯一约定：<b>请求体格式由 <see cref="AxiosConfig.ContentType"/> 决定，与 data 的运行时类型无关</b>。
/// 传入字典不会再被「猜」成表单，对象也不会因为实现了 IEnumerable 而改变编码方式。
/// </para>
/// <list type="bullet">
/// <item>string / byte[] / ReadOnlyMemory&lt;byte&gt; / Stream / HttpContent：内容原样发送，只应用配置的 Content-Type（HttpContent 自带时保留）。</item>
/// <item>结构化数据（对象、字典、dynamic、集合）：json → JSON 文本；x-www-form-urlencoded → 键值对表单；multipart/form-data → 字段 + 文件部件；其余类型仍以 JSON 文本写入。</item>
/// </list>
/// </summary>
internal static class RequestBodyFactory
{
    private const string MultipartMediaType = "multipart/form-data";
    private const string OctetStreamMediaType = "application/octet-stream";

    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 请求体文本编码：默认 UTF-8（axios 行为），可用 config.RequestEncoding 覆盖。
        // charset 只在显式设置 RequestEncoding 时写入，避免给 json 强加 charset 参数。
        var encoding = config.RequestEncoding ?? Encoding.UTF8;
        var appendCharset = config.RequestEncoding is not null;
        var mediaType = config.ContentType;

        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 已构造好的内容（含 MultipartFormDataContent）原样透传；
                // 仅当内容自身没有 Content-Type 且用户显式配置过时才补头部。
                if (config.ContentTypeSet && existing.Headers.ContentType is null)
                {
                    SetContentType(existing, mediaType);
                }

                return existing;

            case string text:
                return CreateText(text, mediaType, encoding, appendCharset);

            case byte[] bytes:
                return CreateBinary(bytes, mediaType);

            case ReadOnlyMemory<byte> memory:
                return CreateBinary(memory.ToArray(), mediaType);

            case Stream stream:
                var streamContent = new StreamContent(stream);
                SetContentType(streamContent, mediaType);
                return streamContent;
        }

        return CreateStructured(data, config, mediaType, encoding, appendCharset);
    }

    private static HttpContent CreateText(string text, string mediaType, Encoding encoding, bool appendCharset)
    {
        // 直接构造 ByteArrayContent 可保证携带 Content-Length，避免分块传输带来的兼容性问题。
        var content = new ByteArrayContent(encoding.GetBytes(text));
        SetContentType(content, ComposeMediaType(mediaType, encoding, appendCharset));
        return content;
    }

    private static HttpContent CreateBinary(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        SetContentType(content, mediaType);
        return content;
    }

    private static HttpContent CreateStructured(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        if (IsMediaType(mediaType, AxiosConfig.FormUrlEncodedContentType))
        {
            return CreateFormContent(data, config, mediaType, encoding, appendCharset);
        }

        if (IsMediaType(mediaType, MultipartMediaType))
        {
            return CreateMultipartContent(data, mediaType, encoding, config);
        }

        // JSON 请求体走可插拔序列化器（默认 System.Text.Json，可换成 Newtonsoft 等）。
        var json = AxiosJson.Get(config).Serialize(data, data.GetType(), config);
        return CreateText(json, mediaType, encoding, appendCharset);
    }

    /// <summary>
    /// 表单/multipart 展平需要 JSON 树语义：默认实现直接 SerializeToElement（零额外解析），
    /// 自定义序列化器则先序列化为 JSON 文本再解析，保证命名策略与转换器仍然生效。
    /// </summary>
    private static JsonElement ToJsonElement(object data, AxiosConfig config)
    {
        var serializer = AxiosJson.Get(config);
        if (serializer is SystemTextJsonSerializer systemTextJson)
        {
            return systemTextJson.ToElement(data, data.GetType(), config);
        }

        var json = serializer.Serialize(data, data.GetType(), config);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpContent CreateFormContent(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        // 先按当前 JSON 选项序列化为 JSON 树，再展平成键值对：
        // 这样命名策略、[JsonPropertyName]、自定义转换器、忽略规则都与 JSON 请求体保持一致。
        var element = ToJsonElement(data, config);

        var pairs = new List<KeyValuePair<string, string>>(8);
        Flatten(null, element, pairs, encoding, config.FormFieldNameSelector);

        // 表单体按「已转义的 ASCII 串」写入，因此编码只影响转义映射（默认 UTF-8 百分号编码，与 axios 一致）。
        var body = string.Join('&', pairs.Select(pair => pair.Key + "=" + pair.Value));

        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        var declared = ComposeMediaType(mediaType, encoding, appendCharset);
        SetContentType(content, declared);
        return content;
    }

    /// <summary>
    /// 构建 multipart/form-data：标量/嵌套对象 → 普通字段，<see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件。
    /// 数组/集合展开为「同名重复部件」（ASP.NET Core、FastAPI、PHP 等都能按同名集合绑定）。
    /// </summary>
    private static HttpContent CreateMultipartContent(
        object data,
        string mediaType,
        Encoding encoding,
        AxiosConfig config)
    {
        // 用户显式声明 boundary 时沿用（部分服务端会校验），否则由 MultipartFormDataContent 生成随机边界。
        var boundary = ExtractBoundary(mediaType);
        var content = boundary is null
            ? new MultipartFormDataContent()
            : new MultipartFormDataContent(boundary);

        try
        {
            var policy = AxiosJson.GetOptions(config).PropertyNamingPolicy;
            CollectParts(null, data, content, encoding, policy, config.FormFieldNameSelector);
            return content;
        }
        catch
        {
            // 构建中途失败：已加入的部件（含已打开的 FileStream）必须释放，避免句柄泄漏。
            content.Dispose();
            throw;
        }
    }

    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
        JsonNamingPolicy? policy,
        Func<string?, string, string>? nameSelector)
    {
        switch (value)
        {
            case null:
                return;

            case FormFile file:
            {
                var part = new StreamContent(file.Content);
                SetContentType(part, file.ContentType ?? OctetStreamMediaType);
                content.Add(part, RequireName(file.FieldName ?? name), file.FileName);
                return;
            }

            case HttpContent part:
                // 调用方自带部件内容（可自行控制 Content-Type / 文件名）
                content.Add(part, RequireName(name));
                return;

            case JsonElement element:
                CollectJsonElementParts(name, element, content, encoding, nameSelector);
                return;

            case string text:
                content.Add(new StringContent(text, encoding), RequireName(name));
                return;

            case byte[] bytes:
            {
                var part = new ByteArrayContent(bytes);
                SetContentType(part, OctetStreamMediaType);
                var field = RequireName(name);
                content.Add(part, field, field);
                return;
            }

            case Stream stream:
            {
                var part = new StreamContent(stream);
                SetContentType(part, OctetStreamMediaType);
                var field = RequireName(name);
                content.Add(part, field, field);
                return;
            }

            case IDictionary<string, object?> genericDictionary:
                // 含 ExpandoObject（dynamic）
                foreach (var entry in genericDictionary)
                {
                    CollectParts(CombineName(nameSelector, name, entry.Key), entry.Value, content, encoding, policy, nameSelector);
                }

                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is null)
                    {
                        continue;
                    }

                    CollectParts(
                        CombineName(nameSelector, name, entry.Key.ToString() ?? string.Empty),
                        entry.Value,
                        content,
                        encoding,
                        policy,
                        nameSelector);
                }

                return;
        }

        var type = value.GetType();
        if (IsScalar(type))
        {
            content.Add(new StringContent(ScalarToString(value), encoding), RequireName(name));
            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                CollectParts(name, item, content, encoding, policy, nameSelector);
            }

            return;
        }

        // 嵌套 POCO：public 可读属性，命名策略 / [JsonPropertyName] / [JsonIgnore] 与 JSON 请求体保持一致。
        foreach (var property in ReflectionCache.GetReadableProperties(type))
        {
            if (IsIgnored(property))
            {
                continue;
            }

            CollectParts(
                CombineName(nameSelector, name, ResolveName(property, policy)),
                property.GetValue(value),
                content,
                encoding,
                policy,
                nameSelector);
        }
    }

    private static void CollectJsonElementParts(
        string? name,
        JsonElement element,
        MultipartFormDataContent content,
        Encoding encoding,
        Func<string?, string, string>? nameSelector)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectJsonElementParts(
                        CombineName(nameSelector, name, property.Name),
                        property.Value,
                        content,
                        encoding,
                        nameSelector);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonElementParts(name, item, content, encoding, nameSelector);
                }

                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                content.Add(new StringContent(ScalarText(element), encoding), RequireName(name));
                break;

            default:
                // null / undefined：跳过
                break;
        }
    }

    /// <summary>字段名生成：默认 <c>prefix[key]</c>，可通过 <see cref="AxiosConfig.FormFieldNameSelector"/> 覆盖。</summary>
    private static string CombineName(Func<string?, string, string>? selector, string? prefix, string name)
        => selector is not null
            ? selector(prefix, name)
            : string.IsNullOrEmpty(prefix) ? name : prefix + "[" + name + "]";

    private static string RequireName(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        throw new InvalidOperationException(
            "multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
    }

    private static bool IsScalar(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Uri);

    private static string ScalarToString(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        char c => c.ToString(),
        DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string ResolveName(PropertyInfo property, JsonNamingPolicy? policy)
    {
        var explicitName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        return explicitName ?? policy?.ConvertName(property.Name) ?? property.Name;
    }

    private static bool IsIgnored(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
        return attribute is not null && attribute.Condition == JsonIgnoreCondition.Always;
    }

    /// <summary>从媒体类型参数里取 boundary（支持带引号的形式）。</summary>
    private static string? ExtractBoundary(string mediaType)
    {
        const string marker = "boundary=";
        var index = mediaType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = mediaType[(index + marker.Length)..].Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        var separator = value.IndexOf(';');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
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
                        CombineName(nameSelector, prefix, property.Name),
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
                    EncodeValue(ScalarText(element), encoding)));
                break;

            default:
                // null / undefined：与 axios 一致，直接跳过。
                break;
        }
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText()
    };

    /// <summary>取媒体类型主值（忽略参数），用于判定编码方式。</summary>
    private static bool IsMediaType(string mediaType, string expected)
    {
        var end = mediaType.IndexOf(';');
        var main = (end < 0 ? mediaType : mediaType[..end]).Trim();
        return string.Equals(main, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 组合实际写入的 Content-Type：媒体类型已自带 charset 时尊重用户；
    /// 文本类媒体类型在显式设置 RequestEncoding 时补 charset，二进制类型不补。
    /// </summary>
    private static string ComposeMediaType(string mediaType, Encoding encoding, bool appendCharset)
    {
        if (!appendCharset
            || mediaType.Contains("charset=", StringComparison.OrdinalIgnoreCase)
            || !IsTextual(mediaType))
        {
            return mediaType;
        }

        return mediaType + "; charset=" + encoding.WebName;
    }

    private static bool IsTextual(string mediaType)
    {
        var end = mediaType.IndexOf(';');
        var main = (end < 0 ? mediaType : mediaType[..end]).Trim();

        return main.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || main.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || main.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || main.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || main.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || main.Equals(AxiosConfig.FormUrlEncodedContentType, StringComparison.OrdinalIgnoreCase)
            || main.Equals("application/javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetContentType(HttpContent content, string mediaType)
    {
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
    }

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
