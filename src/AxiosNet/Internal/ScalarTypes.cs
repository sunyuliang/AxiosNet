namespace AxiosNet.Internal;

/// <summary>
/// 标量判定（类型形态）：基元类型、枚举、string、decimal、日期时间族（DateTime / DateTimeOffset /
/// DateOnly / TimeOnly）、TimeSpan、Guid、Uri 及其可空形态。
/// </summary>
internal static class ScalarTypes
{
    /// <summary>是否为标量类型（含 <see cref="Nullable{T}"/> 形态）；这是表单 / 查询 / 响应兜底共用的判定。</summary>
    public static bool IsScalarType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        return t.IsPrimitive
            || t.IsEnum
            || t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
            || t == typeof(TimeSpan)
            || t == typeof(Guid)
            || t == typeof(Uri);
    }
}
