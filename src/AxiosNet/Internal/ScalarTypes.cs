namespace AxiosNet.Internal;

/// <summary>
/// 标量判定（类型形态）：基元类型、枚举、string、decimal、日期时间族（DateTime / DateTimeOffset /
/// DateOnly / TimeOnly）、TimeSpan、Guid、Uri 及其可空形态。
/// <para>
/// 唯一权威定义：查询参数序列化（<c>ParamsSerializer</c>）与声明式模型的 Path/Header 绑定校验、
/// 默认 Body 护栏共用，杜绝两处清单漂移（曾因各写一份漏掉 DateOnly / TimeOnly / Uri，
/// 导致 POST 模型的单个 DateOnly 属性被静默当成请求体）。
/// </para>
/// </summary>
internal static class ScalarTypes
{
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
