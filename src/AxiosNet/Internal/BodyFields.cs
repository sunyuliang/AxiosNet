using System.Globalization;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 请求体展平（表单 / multipart）共用的「值分类 + 字段命名」：哪些类型算标量、标量怎么取文本、嵌套字段名怎么拼。
/// 命名只看 <see cref="AxiosConfig.SerializationSettings.FormFieldNameSelector"/> 与
/// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>，忽略看 <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>。
/// </summary>
internal static class BodyFields
{
    /// <summary>表单 / multipart 的标量判定，转调 <see cref="ScalarTypes.IsScalarType"/>（唯一权威清单）。</summary>
    public static bool IsScalar(Type type) => ScalarTypes.IsScalarType(type);

    /// <summary>
    /// 表单 / multipart 的标量文本：日期时间折成 UTC 毫秒（与 query 序列化约定一致），
    /// <see cref="DateTimeKind.Unspecified"/> 按 UTC 解释。
    /// </summary>
    public static string ScalarToString(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        char c => c.ToString(),
        DateTime dt => (dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>请求体标量的文本形态（不进序列化器）：日期时间用往返格式 <c>O</c> 保留 Kind 与 tick。</summary>
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

    /// <summary>字段名生成：默认 <c>prefix[key]</c>，可通过 <see cref="AxiosConfig.SerializationSettings.FormFieldNameSelector"/> 覆盖。</summary>
    public static string CombineName(Func<string?, string, string>? selector, string? prefix, string name)
        => selector is not null
            ? selector(prefix, name)
            : string.IsNullOrEmpty(prefix) ? name : prefix + "[" + name + "]";
}
