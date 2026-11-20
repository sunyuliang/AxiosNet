using System.Collections;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using System.Text;
using AxiosNet.Internal.Content;
using AxiosNet.Internal.Serialization;
using AxiosNet.Internal.Url;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）。
/// </summary>
/// <remarks>
/// 选项取 <see cref="AxiosConfig.SerializationSettings.JsonOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// net8.0 目标在配了 <c>TypeInfoResolver</c> 时走 <c>JsonSerializerOptions.GetTypeInfo</c>（AOT 元数据）；
/// 没配时（以及 net6.0 目标，它没有该 API）回退反射重载，见 <see cref="AxiosNet.Reflection.JsonSerializerJit"/>。
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
        // null 请求体与类型无关：不需要任何元数据，按 STJ 默认输出 null（否则空体会被误报为缺元数据）。
        if (value is null)
        {
            // 但 null 传给非可空值类型仍是非法调用：判定放在两个目标框架共用的这里，
            // 否则 net6.0 会由 STJ 的 Type 重载抛出 JsonException，与 net8.0 的异常类型不一致。
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new System.ArgumentNullException(nameof(value), $"类型 {type.FullName} 是非可空值类型，不能序列化 null。");
            }

            return "null";
        }

        return SerializeCore(value, type, AxiosJson.GetOptions(config));
    }

    /// <summary>
    /// JSON 的字节出口：直接产出 UTF-8 正文，不经文本中转。
    /// </summary>
    /// <remarks>
    /// 生效编码不是 UTF-8 时返回 <c>null</c>（显式 <c>Serialization.TextEncoding</c>，或 <c>Content-Type</c>
    /// 声明了非 UTF-8 的 charset）：那两种情况由文本入口负责编码与 charset 头。
    /// 返回非 <c>null</c> 时正文完全由这份字节决定，<c>Content-Type</c> 与文本入口写出的一致。
    /// </remarks>
    public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config)
    {
        if (!EmitsUtf8(config))
        {
            return null;
        }

        if (value is null)
        {
            // 与文本入口同一套合法性判定，避免两条出口对「null + 非可空值类型」给出不同异常。
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
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in body.Members)
            {
                if (member.Spread)
                {
                    WriteSpread(writer, member, options, config, names);
                    continue;
                }

                var name = ResolveAggregateName(member, options);
                EnsureUniqueName(names, name, config);
                writer.WritePropertyName(name);
                WriteAggregateValue(writer, member, options, config);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>聚合正文的字节出口：生效编码不是 UTF-8 时返回 <c>null</c>，由文本入口负责编码与 charset 头。</summary>
    internal static ReadOnlyMemory<byte>? SerializeAggregateToBytes(AggregateBody body, AxiosConfig config)
        => EmitsUtf8(config) ? SerializeAggregate(body, config) : null;

    /// <summary>聚合正文的文本出口（非 UTF-8 编码时由正文工厂按生效编码重新编码）。</summary>
    internal static string SerializeAggregateText(AggregateBody body, AxiosConfig config)
        => Encoding.UTF8.GetString(SerializeAggregate(body, config));

    /// <summary>多个 [RootBody] 的展开：JSON 对象把属性铺到当前层，其它形态（标量 / 数组等）按键写入。</summary>
    private static void WriteSpread(
        Utf8JsonWriter writer,
        AggregateBodyMember member,
        JsonSerializerOptions options,
        AxiosConfig config,
        HashSet<string> names)
    {
        if (member.Value is null)
        {
            return;
        }

        // 先按声明类型序列化一次，再判断根是对象还是其它 JSON 形态。
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
            EnsureUniqueName(names, name, config);
            writer.WritePropertyName(name);
            root.WriteTo(writer);
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            EnsureUniqueName(names, property.Name, config);
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }

    /// <summary>合并到同一层后的键名必须唯一：重复即报错，避免 JSON 对象里出现两个同名属性。</summary>
    private static void EnsureUniqueName(HashSet<string> names, string name, AxiosConfig config)
    {
        if (!names.Add(name))
        {
            throw AxiosException.InvalidOption(
                $"请求体合并后出现重复键 \"{name}\"：请改名，或把 [RootBody] 换成 [Body] 包成独立键。", config);
        }
    }

    /// <summary>聚合正文的键名：显式 [JsonPropertyName] 原样；否则套生效的命名策略（默认 Web 的 camelCase）。</summary>
    private static string ResolveAggregateName(AggregateBodyMember member, JsonSerializerOptions options)
        => member.NameFromAttribute || options.PropertyNamingPolicy is null
            ? member.Name
            : options.PropertyNamingPolicy.ConvertName(member.Name);

    /// <summary>写一个成员值：null 原样、成员级 [Format] 转文本、其余按声明类型交 STJ。</summary>
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

        if (member.Format is { } format && TryWriteFormatted(writer, member.Value, format, config))
        {
            return;
        }

        SerializeAggregateMember(writer, member.Value, member.ValueType, options);
    }

    /// <summary>成员级 [Format] 的 JSON 形态：标量 / 二进制 → 字符串，集合 → 字符串数组；字典与结构化对象交给序列化器。</summary>
    private static bool TryWriteFormatted(
        Utf8JsonWriter writer,
        object value,
        ValueFormat format,
        AxiosConfig config)
    {
        var formatter = config.SerializationOrNull?.ValueText;

        if (value is string || ScalarTypes.IsScalarType(value.GetType()) || ScalarTypes.IsBinaryType(value.GetType()))
        {
            writer.WriteStringValue(ValueText.Format(value, ValueTextChannel.ScalarBody, format, formatter));
            return true;
        }

        if (value is IEnumerable enumerable && !QueryMap.CanEnumerate(value))
        {
            writer.WriteStartArray();
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                writer.WriteStringValue(ValueText.Format(item, ValueTextChannel.ScalarBody, format, formatter));
            }

            writer.WriteEndArray();
            return true;
        }

        return false;
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
            JsonSerializer.Serialize(writer, value, Resolve(options, type));
            return;
        }
#endif
        JsonSerializerJit.EnsureReflectionMetadata(type);
        JsonSerializerJit.SerializeToWriterLegacy(writer, value, type, options);
    }

    /// <summary>
    /// 正文是否一定按 UTF-8 产出：显式指定了 <c>Serialization.TextEncoding</c>，或 <c>Content-Type</c>
    /// 声明了非 UTF-8 的 charset 时为 <c>false</c>。
    /// </summary>
    private static bool EmitsUtf8(AxiosConfig config)
    {
        // 显式编码由文本入口负责（连同 charset 头）：哪怕写的就是 UTF-8，也走文本路径保持一致。
        if (config.SerializationOrNull?.TextEncoding is not null)
        {
            return false;
        }

        // 声明的 charset 优先于库默认值，非 UTF-8 时正文必须按它编码。
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

        // 非文本 Content-Type 也可能声明 charset：与接口默认实现保持同一优先级。
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
    /// 取声明类型的 <see cref="JsonTypeInfo"/>；缺元数据时按「最近祖先」回退。
    /// 只有在调用方显式配置了 <see cref="JsonSerializerOptions.TypeInfoResolver"/>（源生成 / 组合解析器）时才走这里。
    /// </summary>
    private static JsonTypeInfo Resolve(JsonSerializerOptions options, Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current == typeof(object) && type != typeof(object))
            {
                // object 的元数据只描述 object：拿它序列化派生实例会按「未注册的运行时类型」报错，
                // 而且错误文本指向 STJ 内部，不如直接报缺元数据。
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
