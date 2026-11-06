namespace AxiosNet.Internal;

/// <summary>
/// 请求体展平（表单 / multipart）共用的字段名拼装：默认 <c>prefix[key]</c>，可被
/// <see cref="AxiosConfig.SerializationSettings.FormFieldName"/> 覆盖。
/// 值分类归 <see cref="ScalarTypes"/>，值 → 文本归 <see cref="ValueText"/>，本类不持有这两套规则。
/// </summary>
/// <remarks>
/// 字段名默认取 <see cref="ShapeMember.FieldName"/>（构建期已按 <c>[JsonPropertyName]</c> 定好），
/// 成员是否忽略由 <see cref="ShapeMember.Ignored"/> 决定。
/// </remarks>
internal static class BodyFields
{
    /// <summary>字段名生成：默认 <c>prefix[key]</c>，可通过 <see cref="AxiosConfig.SerializationSettings.FormFieldName"/> 覆盖。</summary>
    public static string CombineName(Func<string?, string, string>? selector, string? prefix, string name)
        => selector is not null
            ? selector(prefix, name)
            : string.IsNullOrEmpty(prefix) ? name : prefix + "[" + name + "]";
}
