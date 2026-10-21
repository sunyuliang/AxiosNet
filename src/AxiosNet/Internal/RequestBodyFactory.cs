using System.Collections;
using System.Globalization;
using System.Net.Http.Headers;
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
/// <para>
/// 唯一的例外是「调用方没表态」：未显式设置 ContentType 时，头部采用生效序列化器自报的
/// <see cref="IContentSerializer.MediaType"/>（注入 XML / protobuf 序列化器即自动配对头与正文）。
/// 显式设置过则一律以调用方为准。
/// </para>
/// <list type="bullet">
/// <item>string / byte[] / ReadOnlyMemory&lt;byte&gt; / Memory&lt;byte&gt; / ArraySegment&lt;byte&gt; / Stream / HttpContent：内容原样发送，只应用配置的 Content-Type（HttpContent 自带时保留）。</item>
/// <item>基础数据类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…，见 <c>IsScalar</c>）：直接取文本写入，<b>不经过序列化器</b>。</item>
/// <item>结构化数据（对象、字典、dynamic、集合）：json → 当前序列化器；x-www-form-urlencoded → 键值对表单；multipart/form-data → 字段 + 文件部件。</item>
/// <item>Content-Type 为 multipart/form-data 而 data 是原始内容（无 boundary 参数）时抛 <c>ERR_BAD_OPTION_VALUE</c>：头部与正文自相矛盾，服务端无法解析。</item>
/// </list>
/// </summary>
internal static class RequestBodyFactory
{
    private const string MultipartMediaType = AxiosContentType.MultipartFormData;
    private const string OctetStreamMediaType = AxiosContentType.OctetStream;

    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 请求体文本编码：默认 UTF-8（axios 行为），可用 config.RequestEncoding 覆盖。
        // charset 只在显式设置 RequestEncoding 时写入，避免给 json 强加 charset 参数。
        var encoding = config.RequestEncoding ?? Encoding.UTF8;
        var appendCharset = config.RequestEncoding is not null;
        var mediaType = config.ContentType;

        // multipart/form-data 与「原始内容」请求体是矛盾的组合：头部声明了 multipart，
        // 正文却是裸 string / byte[] / Stream——没有 boundary 参数时服务端无从解析，
        // 带上 boundary 也说明不了字段名。结构化数据才会被组装成 multipart 字段；
        // 要发送预序列化的 multipart 正文，请用 HttpContent 作为 data。
        if (data is not null and not HttpContent
            && IsRawContent(data)
            && IsMediaType(mediaType, MultipartMediaType)
            && ExtractBoundary(mediaType) is null)
        {
            throw AxiosException.InvalidOption(
                "Content-Type 为 multipart/form-data 时，data 不能是原始内容"
                + "（string / byte[] / Memory / Stream）：请传入对象或字典由库组装 multipart 字段，"
                + "或用 HttpContent（如 MultipartFormDataContent）承载预序列化的 multipart 正文。",
                config);
        }

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

                // multipart 没有 boundary 服务端必然解析失败（正文里就是靠随机边界切分部件的）。
                // 自带边界时（MultipartFormDataContent / 手写头部）透传；否则说明调用方既没让库
                // 组装字段、也没准备合法的 multipart 正文 —— 与其发出去换一个看不懂的 400，不如在这里拦。
                if (existing.Headers.ContentType?.MediaType is { } existingMediaType
                    && existingMediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                    && !HasMultipartBoundary(existing.Headers.ContentType))
                {
                    throw AxiosException.InvalidOption(
                        "Content-Type 为 multipart 却没有 boundary：data 传入的 HttpContent 必须自带合法边界"
                        + "（如 MultipartFormDataContent），或把对象/字典交给库组装字段。",
                        config);
                }

                return existing;

            case string text:
                return CreateText(text, mediaType, encoding, appendCharset);

            case byte[] bytes:
                return CreateBinary(bytes, mediaType);

            case ReadOnlyMemory<byte> memory:
                return CreateBinary(memory, mediaType);

            case Memory<byte> memory:
                return CreateBinary(memory, mediaType);

            case ArraySegment<byte> segment:
                return CreateBinary(segment.AsMemory(), mediaType);

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

    /// <summary>
    /// 零拷贝发送 <see cref="ReadOnlyMemory{T}"/>：net6.0 没有内置的 ReadOnlyMemoryContent
    /// （net7 才引入），而 ByteArrayContent 需要先 ToArray()——大请求体上这是一次整段复制。
    /// </summary>
    private static HttpContent CreateBinary(ReadOnlyMemory<byte> memory, string mediaType)
    {
        var content = new ReadOnlyMemoryContent(memory);
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

        var type = data.GetType();

        // 基础数据类型（IsScalar：数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）<b>不经过序列化器</b>：
        // 请求体语义就是「这个值本身」，直接取其文本发起。
        // 交给序列化器既多余（JSON 的裸标量 42 / true 与这里的文本完全一致）又不可行
        // （XML / YAML 序列化器对根标量无从下手：没有元素名），那会逼每个自定义实现各写一遍标量特判。
        // 注意：字典 / 集合 / 对象 / dynamic 一律<b>不进这个分支</b> —— 它们和业务对象是同一套逻辑，
        // 走下面的序列化器（字典不会因为实现了 IDictionary 就被当成标量或表单）。
        if (IsScalar(type))
        {
            return CreateText(ScalarBodyText(data), mediaType, encoding, appendCharset);
        }

        // 其余（json / xml / yaml … 的对象、字典、dynamic、集合）交给生效的序列化器：
        // 显式注入优先，未注入时按 mediaType 自动匹配注册表（再回落内置 JSON）。
        // 库只知道「业务对象 → 文本」，二进制载荷请由调用方序列化成 byte[] / Stream 作为 data 传入。
        var serializer = AxiosContent.Get(config, mediaType);

        // 头由正文的产出者自报 —— 与 axios 默认 transformRequest 里的
        // headers.setContentType('application/json', false) 同构：调用方没显式设置 Content-Type 时
        // 采用序列化器声明的媒体类型（注入 protobuf / XML 序列化器后头会自动跟上）；
        // 显式设置过就绝不覆盖，那是调用方对服务端的判断。
        if (!config.ContentTypeSet && serializer.MediaType is { } declared && !string.IsNullOrWhiteSpace(declared))
        {
            mediaType = declared.Trim();
        }

        return CreateText(serializer.Serialize(data, type, config), mediaType, encoding, appendCharset);
    }

    private static HttpContent CreateFormContent(
        object data,
        AxiosConfig config,
        string mediaType,
        Encoding encoding,
        bool appendCharset)
    {
        // 表单是独立的编码通道，与序列化器无关：直接按对象结构展平（详见 CollectFormPairs），
        // 命名只看 FormFieldNameSelector 与属性上的 [JsonPropertyName]。
        var pairs = new List<KeyValuePair<string, string>>(8);
        CollectFormPairs(null, data, pairs, encoding, config.FormFieldNameSelector);

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
            CollectParts(null, data, content, encoding, config.FormFieldNameSelector);
            return content;
        }
        catch (Exception ex)
        {
            // 构建中途失败：已加入的部件（含已打开的 FileStream）必须释放，避免句柄泄漏。
            content.Dispose();

            // 字段名非法是「调用方给的选项有误」，不是序列化机制出错：
            // 换成带配置上下文的 ERR_BAD_OPTION_VALUE，消息里已指明是哪个名字、哪个字符。
            if (ex is InvalidFieldNameException invalid)
            {
                throw AxiosException.InvalidOption(invalid.Message, config, invalid);
            }

            throw;
        }
    }

    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
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
                // 调用方自带部件内容（可自行控制 Content-Type / 文件名）：
                // MultipartFormDataContent.Add 在部件已带 Content-Disposition 时完全不碰它
                // （实测 net6.0/net8.0），因此这里只补缺失的 Name，其余（尤其 filename）原样保留。
                AddUserPart(content, part, RequireName(name));
                return;

            case JsonElement element:
                CollectJsonElementParts(name, element, content, encoding, nameSelector);
                return;

            case string text:
                content.Add(new StringContent(text, encoding), RequireName(name));
                return;

            case byte[] bytes:
                AddFilePart(content, name, new ByteArrayContent(bytes));
                return;

            case ReadOnlyMemory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory));
                return;

            case Memory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory));
                return;

            case ArraySegment<byte> segment:
                AddFilePart(content, name, new ReadOnlyMemoryContent(segment.AsMemory()));
                return;

            case Stream stream:
                AddFilePart(content, name, new StreamContent(stream));
                return;

            case IDictionary<string, object?> genericDictionary:
                // 含 ExpandoObject（dynamic）
                foreach (var entry in genericDictionary)
                {
                    CollectParts(CombineName(nameSelector, name, entry.Key), entry.Value, content, encoding, nameSelector);
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
                CollectParts(name, item, content, encoding, nameSelector);
            }

            return;
        }

        // 嵌套 POCO：public 可读属性；字段名只看 [JsonPropertyName] 与属性名，忽略看 [JsonIgnore]。
        // 这里刻意不读 JsonSerializerOptions.PropertyNamingPolicy：表单与序列化器解耦，
        // 换成 XML / YAML 序列化器时字段名不能跟着变。
        foreach (var property in ReflectionCache.GetReadableProperties(type))
        {
            if (IsIgnored(property))
            {
                continue;
            }

            CollectParts(
                CombineName(nameSelector, name, ResolveFieldName(property)),
                property.GetValue(value),
                content,
                encoding,
                nameSelector);
        }
    }

    /// <summary>
    /// 把结构化数据展平为表单键值对：嵌套对象 <c>a[b]=v</c>、数组 <c>a[]=v</c>（根数组用 <c>[i]</c>）、
    /// null 跳过、标量取原始文本。
    /// <para>
    /// 直接读对象结构，<b>不经过序列化器</b>：表单是独立编码通道，换个 JSON/XML/YAML 序列化器
    /// 不该改变表单里的字段名与内容。命名只看 <c>FormFieldNameSelector</c> 与 <c>[JsonPropertyName]</c>。
    /// </para>
    /// <para>
    /// 传入 <see cref="JsonElement"/> 时按树语义展平（已经是解析好的树，没必要再反射一遍）。
    /// </para>
    /// </summary>
    private static void CollectFormPairs(
        string? prefix,
        object? value,
        List<KeyValuePair<string, string>> pairs,
        Encoding encoding,
        Func<string?, string, string>? nameSelector)
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

            // urlencoded 没有二进制表示：用 base64（JSON 形态下字节也是字符串化的）。
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
                // 含 ExpandoObject（dynamic）
                foreach (var entry in genericDictionary)
                {
                    CollectFormPairs(
                        CombineName(nameSelector, prefix, entry.Key),
                        entry.Value,
                        pairs,
                        encoding,
                        nameSelector);
                }

                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is null)
                    {
                        continue;
                    }

                    CollectFormPairs(
                        CombineName(nameSelector, prefix, entry.Key.ToString() ?? string.Empty),
                        entry.Value,
                        pairs,
                        encoding,
                        nameSelector);
                }

                return;
        }

        var type = value.GetType();
        if (IsScalar(type))
        {
            AddPair(prefix, ScalarToString(value), pairs, encoding);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                // 根数组没有键名，用下标保证可还原；嵌套数组沿用 a[] 形式。
                var name = prefix is null ? "[" + index + "]" : prefix + "[]";
                CollectFormPairs(name, item, pairs, encoding, nameSelector);
                index++;
            }

            return;
        }

        foreach (var property in ReflectionCache.GetReadableProperties(type))
        {
            if (IsIgnored(property))
            {
                continue;
            }

            CollectFormPairs(
                CombineName(nameSelector, prefix, ResolveFieldName(property)),
                property.GetValue(value),
                pairs,
                encoding,
                nameSelector);
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

    /// <summary>二进制内容一律作为文件部件：字段名同时用作文件名（与 byte[] 既有行为一致）。</summary>
    private static void AddFilePart(MultipartFormDataContent content, string? name, HttpContent part)
    {
        SetContentType(part, OctetStreamMediaType);
        var field = RequireName(name);
        content.Add(part, field, field);
    }

    /// <summary>
    /// 加入调用方自带的部件：已有 Content-Disposition 时只补缺失的 Name
    /// （保留 filename / 类型等一切用户设置），走基类 Add 避免二次改写；
    /// 没有 disposition 时交给 <see cref="MultipartFormDataContent"/> 按 name 生成。
    /// </summary>
    private static void AddUserPart(MultipartFormDataContent content, HttpContent part, string name)
    {
        var disposition = part.Headers.ContentDisposition;
        if (disposition is null)
        {
            content.Add(part, name);
            return;
        }

        if (string.IsNullOrEmpty(disposition.Name))
        {
            disposition.Name = name;
        }

        ((MultipartContent)content).Add(part);
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException(
                "multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
        }

        // 字段名最终写进 Content-Disposition 的 name="..."（quoted-string）。
        // 反斜杠与控制字符不会让 .NET 报错，而是让解析端退回「带引号的原文」——
        // 服务端收到的键名多两个引号，字段静默对不上；引号与换行则直接破坏语法。
        if (DispositionTokens.FindInvalidCharacter(name) is { } invalid)
        {
            throw new InvalidFieldNameException(
                $"multipart 字段名包含非法字符（{invalid}）：'{name}'。"
                + "Content-Disposition 的 name 是 quoted-string：双引号、反斜杠、CR/LF 与其它控制字符"
                + "会破坏语法或让服务端拿到的键名错位。中文可以正常使用（按 RFC 2047 编码），"
                + "请只去掉这些字符。");
        }

        return name;
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

    /// <summary>
    /// 请求体标量的文本形态。<b>不进序列化器</b>（<see cref="IsScalar"/> 命中就走这里）。
    /// <para>
    /// 与表单/查询串的 <see cref="ScalarToString"/> 刻意不同：日期时间用往返格式（<c>O</c>），
    /// 保留 Kind 与 tick —— 请求体是「这个值本身」，把本地时间悄悄折成 UTC、
    /// 或把 tick 截到毫秒都是失真；表单/params 那边受既有约定约束，保持原样。
    /// </para>
    /// </summary>
    private static string ScalarBodyText(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        char c => c.ToString(),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string ResolveFieldName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

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
    /// 内容自身是否已带 multipart boundary。
    /// <para>
    /// 挂在 <see cref="RequestBodyFactory"/> 而不是各调用方：请求体工厂（校验 HttpContent 合法性）
    /// 与 header 应用（multipart 的 Content-Type 不允许被覆盖）用的是同一条判定，不能各写一份。
    /// </para>
    /// </summary>
    internal static bool HasMultipartBoundary(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType is not { } mediaType
            || !mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var parameter in contentType.Parameters)
        {
            if (parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parameter.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>「内容原样发送」的类型：与结构化数据相对，不经过任何展平/序列化。</summary>
    private static bool IsRawContent(object data)
        => data is string or byte[] or ReadOnlyMemory<byte> or Memory<byte> or ArraySegment<byte> or Stream;

    /// <summary>
    /// 组合实际写入的 Content-Type：媒体类型已自带 charset 时尊重用户；
    /// 文本类媒体类型在显式设置 RequestEncoding 时补 charset，二进制类型不补。
    /// 文本 / 二进制的判定与响应侧共用 <see cref="MediaTypes.IsTextual"/>。
    /// </summary>
    private static string ComposeMediaType(string mediaType, Encoding encoding, bool appendCharset)
    {
        if (!appendCharset
            || mediaType.Contains("charset=", StringComparison.OrdinalIgnoreCase)
            || !MediaTypes.IsTextual(mediaType))
        {
            return mediaType;
        }

        return mediaType + "; charset=" + encoding.WebName;
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

/// <summary>
/// 零拷贝的 <see cref="ReadOnlyMemory{T}"/> 请求体内容：net6.0 没有内置的
/// ReadOnlyMemoryContent（net7 才引入），ByteArrayContent 又需要先 ToArray()。
/// </summary>
internal sealed class ReadOnlyMemoryContent : HttpContent
{
    private readonly ReadOnlyMemory<byte> _memory;

    public ReadOnlyMemoryContent(ReadOnlyMemory<byte> memory) => _memory = memory;

    // net6.0 上无令牌的 SerializeToStreamAsync(Stream, TransportContext) 仍是抽象成员，必须实现。
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
        => stream.WriteAsync(_memory, cancellationToken).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _memory.Length;
        return true;
    }
}
