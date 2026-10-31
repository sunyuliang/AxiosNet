using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Serialization;

/// <summary>
/// 库内默认的 <see cref="IContentSerializer"/> 实现（System.Text.Json）：用 <see cref="AxiosConfig.SerializationSettings.JsonSerializerOptions"/>（未设置时用 <see cref="AxiosJson.Options"/>）。
/// net8.0 目标在配了 <c>TypeInfoResolver</c> 时走 <c>JsonSerializerOptions.GetTypeInfo</c>（AOT 元数据）；
/// 没配时（以及 net6.0 目标，它没有该 API）回退反射重载，见 <c>SystemTextJsonSerializer.Reflection.cs</c>。
/// </summary>
public sealed partial class SystemTextJsonSerializer : IContentSerializer
{
    /// <summary>无状态单例，可安全并发共享。</summary>
    public static SystemTextJsonSerializer Instance { get; } = new();

    private SystemTextJsonSerializer()
    {
    }

    /// <summary><c>application/json</c>（与库的 <see cref="AxiosConfig.ContentType"/> 默认值一致）。</summary>
    public string? MediaType => AxiosContentType.Json;

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
                throw new ArgumentNullException(nameof(value), $"类型 {type.FullName} 是非可空值类型，不能序列化 null。");
            }

            return "null";
        }

        return SerializeCore(value, type, AxiosJson.GetOptions(config));
    }

    /// <inheritdoc />
    public object? Deserialize(string json, Type type, AxiosConfig config)
        => DeserializeCore(json, type, AxiosJson.GetOptions(config));

    /// <summary>二进制入口：按 charset / BOM 处理后再反序列化，不做 <c>byte[] → string</c> 中转。</summary>
    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
    {
        var span = data.Span;

        // 非文本 Content-Type 也可能声明 charset：与接口默认实现保持同一优先级。
        var forced = config.SerializationOrNull?.ResponseEncoding;
        var charSet = config.Response?.CharSet;
        if (forced is not null
            || (ResponseTextDecoder.ResolveFromCharset(charSet) is { } declared
                && declared.CodePage != Encoding.UTF8.CodePage))
        {
            return Deserialize(ResponseTextDecoder.Decode(span, charSet, forced), type, config);
        }

        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
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
        EnsureReflectionMetadata(type);
        return SerializeLegacy(value, type, options);
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
        EnsureReflectionMetadata(type);
        return DeserializeLegacy(json, type, options);
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
        EnsureReflectionMetadata(type);
        return DeserializeUtf8Legacy(utf8Json, type, options);
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
