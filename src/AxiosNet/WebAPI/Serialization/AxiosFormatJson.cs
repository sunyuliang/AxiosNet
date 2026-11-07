#if NET7_0_OR_GREATER
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Reflection;

namespace AxiosNet.WebAPI.Serialization;

/// <summary>
/// <see cref="FormatAttribute"/> 的 JSON 桥：让成员上的格式化在结构化正文内部也生效（默认不启用）。
/// </summary>
/// <remarks>
/// 库默认不进入序列化器的文档内部——JSON 里怎么长由 <c>JsonSerializerOptions</c> 决定。
/// 需要「URL 与 JSON 用同一份 <c>[Format]</c>」时，把 <see cref="ApplyTo"/> 的返回值设给
/// <c>Serialization.JsonOptions</c>（或 <c>AxiosJson.Options</c>）。
/// 只支持 net7.0 及以上（依赖 <see cref="JsonTypeInfo.Properties"/> 的属性级改写），读取方向按不变文化尽力还原。
/// </remarks>
public static class AxiosJsonFormat
{
    /// <summary>在现有选项上挂 <c>[Format]</c> 桥，返回新选项（原选项不被修改）。</summary>
    /// <param name="options">基线选项；<c>null</c> 表示 <see cref="AxiosJson.Options"/>。</param>
    [RequiresDynamicCode("[Format] 桥按属性类型构造泛型转换器，Native AOT 下不可用。")]
    [RequiresUnreferencedCode("[Format] 桥读取属性特性并构造转换器，裁剪后可能缺少元数据。")]
    public static JsonSerializerOptions ApplyTo(JsonSerializerOptions? options = null)
    {
        var current = options ?? AxiosJson.Options;
        var clone = new JsonSerializerOptions(current);

        // 已有解析器优先：用户自定义的解析器仍然生效，桥只在其上叠加属性级转换器。
        var inner = current.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        clone.TypeInfoResolver = new AxiosJsonFormatTypeInfoResolver(inner);
        return clone;
    }
}

/// <summary>把 <c>[Format]</c> 翻译成属性级 <see cref="JsonConverter"/> 的类型信息解析器。</summary>
public sealed class AxiosJsonFormatTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly IJsonTypeInfoResolver _inner;

    /// <summary>包装一个已有的解析器。</summary>
    /// <param name="inner">基础解析器（通常是 <see cref="DefaultJsonTypeInfoResolver"/>）。</param>
    public AxiosJsonFormatTypeInfoResolver(IJsonTypeInfoResolver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    /// <remarks>接口实现的标注必须与接口一致，AOT 不可用的事实由 <see cref="AxiosJsonFormat.ApplyTo"/> 承担。</remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "整个 [Format] 桥以 RequiresDynamicCode / RequiresUnreferencedCode 标注为 AOT 不可用。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL3050",
        Justification = "整个 [Format] 桥以 RequiresDynamicCode / RequiresUnreferencedCode 标注为 AOT 不可用。")]
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var info = _inner.GetTypeInfo(type, options);
        if (info is null || info.Kind != JsonTypeInfoKind.Object)
        {
            return info;
        }

        foreach (var property in info.Properties)
        {
            if (property.AttributeProvider?.GetCustomAttributes(typeof(FormatAttribute), inherit: true) is not [FormatAttribute attribute])
            {
                continue;
            }

            property.CustomConverter = FormattedValueConverterFactory.Create(
                property.PropertyType,
                FormatSpecReader.Read(attribute, type.Name, property.Name));
        }

        return info;
    }
}

/// <summary>按属性类型构造 <see cref="FormattedValueConverter{T}"/>（泛型类无法直接调用自己的静态工厂）。</summary>
internal static class FormattedValueConverterFactory
{
    /// <summary>为指定类型造一个 <c>[Format]</c> 转换器（net7+ 的 JSON 桥）。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL3050",
        Justification = "整个 [Format] 桥以 RequiresDynamicCode / RequiresUnreferencedCode 标注为 AOT 不可用。")]
    public static JsonConverter Create(Type type, ValueFormat format)
        => (JsonConverter)Activator.CreateInstance(
            typeof(FormattedValueConverter<>).MakeGenericType(type),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [format],
            culture: null)!;
}

/// <summary>按成员级格式化写出 JSON 字符串，读取时按不变文化还原。</summary>
/// <summary>单个 <c>[Format]</c> 成员的转换器：写走格式化，读按不变文化还原。</summary>
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "整个 [Format] 桥以 RequiresDynamicCode / RequiresUnreferencedCode 标注为 AOT 不可用。")]
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2067",
    Justification = "整个 [Format] 桥以 RequiresDynamicCode / RequiresUnreferencedCode 标注为 AOT 不可用。")]
internal sealed class FormattedValueConverter<T> : JsonConverter<T>
{
    private readonly ValueFormat _format;

    private FormattedValueConverter(ValueFormat format)
    {
        _format = format;
    }

    /// <summary>读：只接受 JSON 字符串，按 <c>[Format]</c> 反解成目标类型（可空类型先剥外层）。</summary>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"[Format] 桥期望字符串，实际是 {reader.TokenType}。");
        }

        var text = reader.GetString() ?? string.Empty;
        var target = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        return (T)Parse(text, target);
    }

    /// <summary>写：null 写 null，其余按 <c>[Format]</c> 转成字符串；格式化返回 null 时回落到通道默认规则。</summary>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // 格式化函数返回 null 时回落到通道默认规则（不变文化）：这里不能再进序列化器（会递归到本转换器）。
        writer.WriteStringValue(ValueText.Format(value, ValueTextChannel.ScalarBody, _format) ?? string.Empty);
    }

    /// <summary>把格式化后的字符串还原成目标类型：<c>string</c> 直通，其余走 <c>TypeConverter</c>。</summary>
    private static object Parse(string text, Type target)
    {
        if (target == typeof(string))
        {
            return text;
        }

        var converter = TypeDescriptor.GetConverter(target);
        if (converter.CanConvertFrom(typeof(string)))
        {
            return converter.ConvertFromInvariantString(text)!;
        }

        return Convert.ChangeType(text, target, CultureInfo.InvariantCulture)!;
    }
}
#endif
