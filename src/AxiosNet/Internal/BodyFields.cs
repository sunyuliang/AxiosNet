using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 请求体展平（表单 / multipart）共用的「值分类 + 字段命名」：
/// 哪些类型算标量、标量怎么取文本、嵌套字段名怎么拼。
/// <para>
/// 命名只看 <see cref="AxiosConfig.FormFieldNameSelector"/> 与属性上的 <see cref="JsonPropertyNameAttribute"/>，
/// 忽略看 <see cref="JsonIgnoreAttribute"/>（<c>Always</c>）；<b>刻意不读</b> 序列化器的命名策略 —— 表单与序列化器解耦。
/// </para>
/// </summary>
internal static class BodyFields
{
    public static bool IsScalar(Type type)
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

    /// <summary>表单 / multipart 的标量文本：日期时间折成 UTC 毫秒（与 query 序列化的既有约定一致）。</summary>
    public static string ScalarToString(object value) => value switch
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
    /// 请求体标量的文本形态。<b>不进序列化器</b>。
    /// <para>
    /// 与表单/查询串的 <see cref="ScalarToString"/> 刻意不同：日期时间用往返格式（<c>O</c>）保留 Kind 与 tick
    /// —— 请求体是「这个值本身」，折成 UTC 或截到毫秒都是失真；表单/params 那边受既有约定约束。
    /// </para>
    /// </summary>
    public static string ScalarBodyText(object value) => value switch
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

    /// <summary>JSON 树叶子节点的文本（数值保留原始书写形式，不经过 double 往返）。</summary>
    public static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText()
    };

    /// <summary>字段名生成：默认 <c>prefix[key]</c>，可通过 <see cref="AxiosConfig.FormFieldNameSelector"/> 覆盖。</summary>
    public static string CombineName(Func<string?, string, string>? selector, string? prefix, string name)
        => selector is not null
            ? selector(prefix, name)
            : string.IsNullOrEmpty(prefix) ? name : prefix + "[" + name + "]";

    /// <summary>属性名在表单 / multipart 里的字段名：<c>[JsonPropertyName]</c> 优先，否则属性名。</summary>
    public static string ResolveFieldName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

    /// <summary>是否整体忽略：只认 <c>[JsonIgnore(Always)]</c>（条件忽略在展平时无法求值，按参与处理）。</summary>
    public static bool IsIgnored(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
        return attribute is not null && attribute.Condition == JsonIgnoreCondition.Always;
    }
}
