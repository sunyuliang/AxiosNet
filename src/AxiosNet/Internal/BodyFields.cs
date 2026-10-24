using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 请求体展平（表单 / multipart）共用的「值分类 + 字段命名」：
/// 哪些类型算标量、标量怎么取文本、嵌套字段名怎么拼。
/// <para>
/// 命名只看 <see cref="AxiosConfig.SerializationSettings.FormFieldNameSelector"/> 与属性上的
/// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>，
/// 忽略看 <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>（<c>Always</c>）；
/// <b>刻意不读</b> 序列化器的命名策略 —— 表单与序列化器解耦。
/// </para>
/// </summary>
internal static class BodyFields
{
    /// <summary>
    /// 表单 / multipart 的标量判定：直接转调 <see cref="ScalarTypes.IsScalarType"/>（唯一权威清单）。
    /// <para>
    /// 这里不再维护第二份清单：历史上表单侧与 Path/Header 校验侧各写一份，漂移的结果是
    /// 「同一个 DateOnly 属性在表单里当标量、在默认 Body 判定里当对象」。运行时传进来的都是装箱后的
    /// 实际类型，因此 <see cref="Nullable{T}"/> 解包由被调方统一负责。
    /// </para>
    /// </summary>
    public static bool IsScalar(Type type) => ScalarTypes.IsScalarType(type);

    /// <summary>表单 / multipart 的标量文本：日期时间折成 UTC 毫秒（与 query 序列化的既有约定一致）。</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="DateTime"/>：<see cref="DateTimeKind.Unspecified"/>（未标注时区，最常见的构造）
    /// 按 <b>UTC</b> 解释 —— 按本机时区折会让同一份数据在不同时区的机器上发出不同字节；
    /// <see cref="DateTimeKind.Local"/> 仍按本机时区转换。</item>
    /// <item><see cref="TimeOnly"/>：显式用往返格式 <c>O</c>（保留秒）—— <see cref="IFormattable"/>
    /// 的默认短时间格式会截掉秒（<c>16:30:15</c> → <c>16:30</c>），与请求体路径的 <c>O</c> 分歧。</item>
    /// <item><see cref="DateOnly"/>：维持 <see cref="IFormattable"/> 的不变文化短日期（既有契约，已固化测试）。</item>
    /// </list>
    /// </remarks>
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

    /// <summary>字段名生成：默认 <c>prefix[key]</c>，可通过 <see cref="AxiosConfig.SerializationSettings.FormFieldNameSelector"/> 覆盖。</summary>
    public static string CombineName(Func<string?, string, string>? selector, string? prefix, string name)
        => selector is not null
            ? selector(prefix, name)
            : string.IsNullOrEmpty(prefix) ? name : prefix + "[" + name + "]";

    /// <summary>属性名在表单 / multipart 里的字段名：<c>[JsonPropertyName]</c> 优先，否则属性名。</summary>
    /// <remarks>特性查找结果按属性缓存（<see cref="ReflectionCache.GetFieldName"/>）：展平路径每请求都要取字段名。</remarks>
    public static string ResolveFieldName(PropertyInfo property) => ReflectionCache.GetFieldName(property);

    /// <summary>是否整体忽略：只认 <c>[JsonIgnore(Always)]</c>（条件忽略在展平时无法求值，按参与处理）。</summary>
    /// <remarks>特性查找结果按属性缓存（<see cref="ReflectionCache.IsIgnoredProperty"/>），与字段名同一出处。</remarks>
    public static bool IsIgnored(PropertyInfo property) => ReflectionCache.IsIgnoredProperty(property);
}
