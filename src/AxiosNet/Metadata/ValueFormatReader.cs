using System.Reflection;

namespace AxiosNet.Metadata;

/// <summary>
/// 成员级 <see cref="ValueFormat"/> 读取器的注册点：内核默认没有任何格式信息（返回 <c>null</c>），
/// 由需要按成员声明格式的调用方在模块初始化时主动注册；内核只读，不关心来源。
/// </summary>
public static class ValueFormatReader
{
    private static Func<PropertyInfo, ValueFormat?>? _reader;

    /// <summary>
    /// 注册成员级格式化读取器；传 <c>null</c> 恢复默认（无格式信息）。
    /// 重复注册以后注册的为准。
    /// </summary>
    /// <param name="reader">按公共属性返回格式化规格；返回 <c>null</c> 表示该成员没有格式声明。</param>
    /// <returns>注册前的读取器，便于调用方临时替换后恢复。</returns>
    public static Func<PropertyInfo, ValueFormat?>? Register(Func<PropertyInfo, ValueFormat?>? reader)
        => Interlocked.Exchange(ref _reader, reader);

    /// <summary>当前生效的读取器；未注册时为 <c>null</c>（即「无格式信息」）。</summary>
    public static Func<PropertyInfo, ValueFormat?>? Current => Volatile.Read(ref _reader);

    /// <summary>读取成员的格式化规格；未注册或该成员没有声明时返回 <c>null</c>。</summary>
    internal static ValueFormat? Read(PropertyInfo property)
        => Volatile.Read(ref _reader)?.Invoke(property);
}
