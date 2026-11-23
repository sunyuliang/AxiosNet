using System.Collections;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using System.Text;
using AxiosNet.Internal.Content;
using AxiosNet.Internal.Serialization;
using AxiosNet.Internal.Url;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Serialization;

/// <summary>库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）。</summary>
/// <remarks>
/// 选项取 <see cref="AxiosConfig.SerializationSettings.JsonOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）；
/// net8.0 配了 <c>TypeInfoResolver</c> 时走 AOT 元数据，否则（含 net6.0）回退反射重载。
/// </remarks>
public sealed class SystemTextJsonSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享。</summary>
    public static SystemTextJsonSerializer Instance { get; } = new();

    private SystemTextJsonSerializer()
    {
    }

    /// <summary><c>application/json</c>（与库的 <see cref="AxiosConfig.ContentType"/> 默认值一致）。</summary>
    public string? MediaType => AxiosContentType.Json;

    /// <summary>序列化 <c>null</c> 的 UTF-8 形态（<c>null</c> 字面量），与文本入口的输出逐字相同。</summary>
    private static readonly byte[] NullLiteralUtf8 = "null"u8.ToArray();

    /// <inheritdoc />
    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        // null 请求体与类型无关，直接输出 null 字面量。
        if (value is null)
        {
            // 非可空值类型传 null 是非法调用。
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new System.ArgumentNullException(nameof(value), $"类型 {type.FullName} 是非可空值类型，不能序列化 null。");
            }

            return "null";
        }

        return SerializeCore(value, type, AxiosJson.GetOptions(config));
    }

    /// <summary>JSON 的字节出口：直接产出 UTF-8 正文，不经文本中转。</summary>
    /// <remarks>生效编码不是 UTF-8 时返回 <c>null</c>（由文本入口负责编码与 charset 头）；返回非 <c>null</c> 时正文完全由这份字节决定。</remarks>
    public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config)
    {
        if (!EmitsUtf8(config))
        {
            return null;
        }

        if (value is null)
        {
            // 与文本入口同一套合法性判定。
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new System.ArgumentNullException(nameof(value), $"类型 {type.FullName} 是非可空值类型，不能序列化 null。");
            }

            return NullLiteralUtf8;
        }

        return SerializeUtf8Core(value, type, AxiosJson.GetOptions(config));
    }

    /// <summary>默认正文（未标注成员按键名聚合）的字节出口：始终按 UTF-8 写出一个 JSON 对象。</summary>
    internal static byte[] SerializeAggregate(AggregateBody body, AxiosConfig config)
    {
        var options = AxiosJson.GetOptions(config);
        var writerOptions = new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented
        };

        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
        {
            writer.WriteStartObject();
            // 多于 1 个键或存在展开时才需要去重集合（大小写不敏感）。
            var names = body.Members.Length > 1 || (body.Members.Length == 1 && body.Members[0].Spread)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var member in body.Members)
            {
                if (member.Spread)
                {
                    WriteSpread(writer, member, options, config, names);
                    continue;
                }

                var name = ResolveAggregateName(member, options);
                AggregateKeys.EnsureUnique(names, name, config, ignoreCase: true);
                writer.WritePropertyName(name);
                WriteAggregateValue(writer, member, options, config);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>多个 [RootBody] 的展开：JSON 对象把属性铺到当前层，其它形态（标量 / 数组等）按键写入。</summary>
    private static void WriteSpread(
        Utf8JsonWriter writer,
        AggregateBodyMember member,
        JsonSerializerOptions options,
        AxiosConfig config,
        HashSet<string>? names)
    {
        if (member.Value is null)
        {
            return;
        }

        // 先按声明类型序列化一次，再判定根是对象还是其它形态。
        using var buffer = new MemoryStream(128);
        using (var inner = new Utf8JsonWriter(buffer))
        {
            SerializeAggregateMember(inner, member.Value, member.ValueType, options);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            var name = ResolveAggregateName(member, options);
            AggregateKeys.EnsureUnique(names, name, config, ignoreCase: true);
            writer.WritePropertyName(name);
            root.WriteTo(writer);
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            AggregateKeys.EnsureUnique(names, property.Name, config, ignoreCase: true);
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }

    /// <summary>聚合正文的键名：显式名字（<c>[Body("名")]</c> / <c>[JsonPropertyName]</c>）原样；否则套生效的命名策略（默认 Web 的 camelCase）。</summary>
    private static string ResolveAggregateName(AggregateBodyMember member, JsonSerializerOptions options)
        => member.NameFromAttribute || options.PropertyNamingPolicy is null
            ? member.Name
            : options.PropertyNamingPolicy.ConvertName(member.Name);

    /// <summary>写一个成员值：null 原样、成员级 [Format] / 实例钩子转文本、其余按声明类型交 STJ。</summary>
    private static void WriteAggregateValue(
        Utf8JsonWriter writer,
        AggregateBodyMember member,
        JsonSerializerOptions options,
        AxiosConfig config)
    {
        if (member.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (TryWriteFormatted(writer, member.Value, member.Format, options, config))
        {
            return;
        }

        SerializeAggregateMember(writer, member.Value, member.ValueType, options);
    }

    /// <summary>
    /// 成员级 <c>[Format]</c> 与实例钩子（<see cref="AxiosConfig.SerializationSettings.ValueText"/>）的 JSON 形态：
    /// 标量 / 二进制 → 字符串，集合 → 字符串数组；未处理（或字典 / 结构化对象）时返回 <c>false</c>。
    /// </summary>
    private static bool TryWriteFormatted(
        Utf8JsonWriter writer,
        object value,
        ValueFormat? format,
        JsonSerializerOptions options,
        AxiosConfig config)
    {
        var formatter = config.SerializationOrNull?.ValueText;
        if (format is null && formatter is null)
        {
            return false;
        }

        if (value is string || ScalarTypes.IsScalarType(value.GetType()) || ScalarTypes.IsBinaryType(value.GetType()))
        {
            if (ValueText.TryCustomFormat(value, ValueTextChannel.ScalarBody, format, formatter) is not { } text)
            {
                return false;
            }

            writer.WriteStringValue(text);
            return true;
        }

        if (value is not IEnumerable enumerable || QueryMap.CanEnumerate(value))
        {
            return false;
        }

        // 集合：成员级 [Format] 逐元素格式化；只有实例钩子时逐个询问，未命中的按原类型写出。
        writer.WriteStartArray();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                writer.WriteNullValue();
                continue;
            }

            if (format is not null)
            {
                writer.WriteStringValue(ValueText.Format(item, ValueTextChannel.ScalarBody, format, formatter));
                continue;
            }

            if (ValueText.TryCustomFormat(item, ValueTextChannel.ScalarBody, memberFormat: null, formatter) is { } text)
            {
                writer.WriteStringValue(text);
                continue;
            }

            SerializeAggregateMember(writer, item, item.GetType(), options);
        }

        writer.WriteEndArray();
        return true;
    }

    /// <summary>按声明类型写入聚合正文的成员值：AOT 走 <c>JsonTypeInfo</c>，JIT 回退反射重载。</summary>
    private static void SerializeAggregateMember(Utf8JsonWriter writer, object? value, Type type, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

#if NET8_0_OR_GREATER
        if (options.TypeInfoResolver is not null)
        {
            // 成员类型没进 AOT 图时退回标量出口，结构化类型则报缺元数据。
            JsonTypeInfo? info = null;
            try
            {
                info = Resolve(options, type);
            }
            catch (NotSupportedException)
            {
                // 交给下面的标量出口。
            }

            if (info is not null)
            {
                JsonSerializer.Serialize(writer, value, info);
                return;
            }

            if (TryWriteScalar(writer, value))
            {
                return;
            }

            throw new NotSupportedException(AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message);
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        JsonSerializerJit.SerializeToWriterLegacy(writer, value, type, options);
    }

    /// <summary>缺 <c>JsonTypeInfo</c> 时的标量出口：写法与 STJ 默认转换器一致，不需要任何元数据。</summary>
    private static bool TryWriteScalar(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                return true;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return true;
            case char character:
                writer.WriteStringValue(character.ToString());
                return true;
            case sbyte or byte or short or ushort or int or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            case long number:
                writer.WriteNumberValue(number);
                return true;
            case ulong number:
                writer.WriteNumberValue(number);
                return true;
            case float number:
                writer.WriteNumberValue(number);
                return true;
            case double number:
                writer.WriteNumberValue(number);
                return true;
            case decimal number:
                writer.WriteNumberValue(number);
                return true;
            case Guid guid:
                writer.WriteStringValue(guid);
                return true;
            case DateTime time:
                writer.WriteStringValue(time);
                return true;
            case DateTimeOffset time:
                writer.WriteStringValue(time);
                return true;
            case TimeSpan span:
                writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture));
                return true;
            case Uri uri:
                writer.WriteStringValue(uri.OriginalString);
                return true;
            case Enum enumeration:
                writer.WriteNumberValue(Convert.ToInt64(enumeration, CultureInfo.InvariantCulture));
                return true;
            default:
                return false;
        }
    }

    /// <summary>正文是否一定按 UTF-8 产出；显式 <c>Serialization.TextEncoding</c> 或 <c>Content-Type</c> 声明非 UTF-8 时为 <c>false</c>。</summary>
    internal static bool EmitsUtf8(AxiosConfig config)
    {
        // 显式编码由文本入口负责，哪怕写的就是 UTF-8。
        if (config.SerializationOrNull?.TextEncoding is not null)
        {
            return false;
        }

        // 声明的 charset 优先于库默认值。
        var declared = RequestContentType.ResolveCharset(config.ContentType, config);
        return declared is null || declared.CodePage == Encoding.UTF8.CodePage;
    }

    /// <inheritdoc />
    public object? Deserialize(string json, Type type, AxiosConfig config)
        => DeserializeCore(json, type, AxiosJson.GetOptions(config));

    /// <summary>二进制入口：按 charset / BOM 处理后再反序列化，不做 <c>byte[] → string</c> 中转。</summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
    {
        var span = data.Span;

        // 显式编码与声明的非 UTF-8 charset 都走文本解码。
        var forced = config.SerializationOrNull?.TextEncoding;
        var charSet = config.Response?.CharSet;
        if (forced is not null
            || (ResponseTextDecoder.ResolveFromCharset(charSet) is { } declared
                && declared.CodePage != Encoding.UTF8.CodePage))
        {
            return Deserialize(ResponseTextDecoder.Decode(span, charSet, forced), type, config);
        }

        if (BomDetection.HasUtf8Bom(span))
        {
            span = span[3..];
        }
        else if (ResponseTextDecoder.DetectFromBom(span) is { } bomEncoding)
        {
            return Deserialize(ResponseTextDecoder.Decode(span, charSet: null, forced: bomEncoding), type, config);
        }

        return DeserializeUtf8Core(span, type, AxiosJson.GetOptions(config));
    }

    /// <summary>唯一的 AOT / 反射分叉：有 <c>TypeInfoResolver</c> 才是 AOT 元数据路径。</summary>
    private static string SerializeCore(object? value, Type type, JsonSerializerOptions options)
    {
#if NET8_0_OR_GREATER
        if (options.TypeInfoResolver is not null)
        {
            return JsonSerializer.Serialize(value, Resolve(options, type));
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        return JsonSerializerJit.SerializeLegacy(value, type, options);
    }

    /// <summary>字节出口的 AOT / 反射分叉，与 <see cref="SerializeCore"/> 同一判据。</summary>
    private static byte[] SerializeUtf8Core(object? value, Type type, JsonSerializerOptions options)
    {
#if NET8_0_OR_GREATER
        if (options.TypeInfoResolver is not null)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, Resolve(options, type));
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        return JsonSerializerJit.SerializeUtf8Legacy(value, type, options);
    }

    /// <summary>唯一的 AOT / 反射分叉：有 <c>TypeInfoResolver</c> 才是 AOT 元数据路径。</summary>
    private static object? DeserializeCore(string json, Type type, JsonSerializerOptions options)
    {
#if NET8_0_OR_GREATER
        if (options.TypeInfoResolver is not null)
        {
            return JsonSerializer.Deserialize(json, Resolve(options, type));
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        return JsonSerializerJit.DeserializeLegacy(json, type, options);
    }

    /// <summary>唯一的 AOT / 反射分叉：有 <c>TypeInfoResolver</c> 才是 AOT 元数据路径。</summary>
    private static object? DeserializeUtf8Core(ReadOnlySpan<byte> utf8Json, Type type, JsonSerializerOptions options)
    {
#if NET8_0_OR_GREATER
        if (options.TypeInfoResolver is not null)
        {
            return JsonSerializer.Deserialize(utf8Json, Resolve(options, type));
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        return JsonSerializerJit.DeserializeUtf8Legacy(utf8Json, type, options);
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// 取声明类型的 <see cref="JsonTypeInfo"/>；缺元数据时回退到最近的有元数据的祖先。
    /// 仅当调用方配置了 <see cref="JsonSerializerOptions.TypeInfoResolver"/> 时走这里。
    /// </summary>
    private static JsonTypeInfo Resolve(JsonSerializerOptions options, Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current == typeof(object) && type != typeof(object))
            {
                // 不用 object 的元数据序列化派生实例。
                break;
            }

            try
            {
                return options.GetTypeInfo(current);
            }
            catch (NotSupportedException)
            {
                // 继续向基类回退
            }
        }

        throw new NotSupportedException(AxiosAotMetadataException.MissingJsonPropertyInfo(type).Message);
    }
#endif
}
