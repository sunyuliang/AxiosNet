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
#if !NETSTANDARD2_0
            // DateOnly / TimeOnly 是 .NET 6 类型，兼容资产上不视作标量。
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
#endif
            || t == typeof(TimeSpan)
            || t == typeof(Guid)
            || t == typeof(Uri);
    }

    /// <summary>
    /// 二进制形态（<see cref="byte"/> 的四种表示）：没有格式串语义，但可以自定义编码，
    /// 也能作为裸标量正文（默认 base64）。
    /// </summary>
    public static bool IsBinaryType(Type type)
        => type == typeof(byte[])
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(Memory<byte>)
            || type == typeof(ArraySegment<byte>);

    /// <summary>可作裸标量正文的类型：标量或二进制形态（两者都能转成一段文本）。</summary>
    public static bool IsScalarBodyType(Type type) => IsScalarType(type) || IsBinaryType(type);
}
