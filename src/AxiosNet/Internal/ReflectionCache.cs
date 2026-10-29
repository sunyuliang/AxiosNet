using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 反射元数据缓存：按类型缓存「可读、非索引器」的属性数组，按属性缓存转义名与字段名，
/// 避免每请求重复反射。
/// </summary>
internal static class ReflectionCache
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

    /// <summary>属性名的转义形态（params / 表单展平的键）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, string> EscapedNames = new();

    /// <summary>属性名的「[转义名]」后缀（嵌套键）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, string> BracketSuffixes = new();

    /// <summary>表单 / multipart 的字段名（<c>[JsonPropertyName]</c> 优先，否则属性名）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, string> FieldNames = new();

    /// <summary>是否整体忽略（<c>[JsonIgnore(Always)]</c>）。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, bool> IgnoredProperties = new();

    /// <summary>flags 枚举的「已定义位掩码」（非 flags 枚举缓存 <c>null</c>）。</summary>
    private static readonly ConcurrentDictionary<Type, EnumFlagMask?> FlagMasks = new();

    /// <summary>类型是否实现 <c>IAsyncEnumerable&lt;T&gt;</c>（只用于错误提示）。</summary>
    private static readonly ConcurrentDictionary<Type, bool> AsyncEnumerableTypes = new();

    /// <param name="type">待展平的类型；标注要求裁剪器保留其公开属性（含 getter）。</param>
    public static PropertyInfo[] GetReadableProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        if (Cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        // 用带标注的 type 直接反射；缓存字典的键不携带裁剪标注，不能把反射放进工厂 lambda。
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        return Cache.GetOrAdd(type, properties);
    }

    /// <summary>属性名转义后的键（根层级，无方括号）。</summary>
    public static string GetEscapedName(PropertyInfo property)
        => EscapedNames.GetOrAdd(property, static p => ParamsSerializer.EscapeKey(p.Name));

    /// <summary>属性名转义后的「[键]」后缀（嵌套层级）。</summary>
    public static string GetBracketSuffix(PropertyInfo property)
        => BracketSuffixes.GetOrAdd(property, static p => "[" + ParamsSerializer.EscapeKey(p.Name) + "]");

    /// <summary>表单 / multipart 字段名：<c>[JsonPropertyName]</c> 优先，否则属性名（按属性缓存）。</summary>
    public static string GetFieldName(PropertyInfo property)
        => FieldNames.GetOrAdd(property, static p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);

    /// <summary>是否整体忽略：只认 <c>[JsonIgnore(Always)]</c>（条件忽略在展平时无法求值，按参与处理）。</summary>
    public static bool IsIgnoredProperty(PropertyInfo property)
        => IgnoredProperties.GetOrAdd(property, static p =>
        {
            var attribute = p.GetCustomAttribute<JsonIgnoreAttribute>();
            return attribute is not null && attribute.Condition == JsonIgnoreCondition.Always;
        });

    /// <summary>
    /// 类型是否实现了任意 <see cref="IAsyncEnumerable{T}"/>（按类型缓存）。只用于把「异步序列不能当正文」
    /// 转成可执行的错误提示；<c>GetInterfaces</c> 会分配数组，所以不能在每个结构化请求上重复调用。
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "只用于生成错误提示：传入的是已实例化对象的运行时类型，它的接口实现不可能被裁掉。")]
    public static bool IsAsyncEnumerable(Type type)
    {
        if (AsyncEnumerableTypes.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var implemented = false;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            implemented = true;
        }
        else
        {
            foreach (var contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                {
                    implemented = true;
                    break;
                }
            }
        }

        return AsyncEnumerableTypes.GetOrAdd(type, implemented);
    }

    /// <summary>
    /// flags 枚举的已定义位掩码；非 flags 枚举返回 <c>null</c>。底层类型为 <c>ulong</c> 的枚举按无符号汇总。
    /// </summary>
    public static EnumFlagMask? GetFlagMask(Type enumType)
        => FlagMasks.GetOrAdd(enumType, static type =>
        {
            if (!type.IsDefined(typeof(FlagsAttribute), inherit: false))
            {
                return null;
            }

            if (Enum.GetUnderlyingType(type) == typeof(ulong))
            {
                var unsignedMask = 0UL;
                foreach (var defined in Enum.GetValues(type))
                {
                    unsignedMask |= Convert.ToUInt64(defined, CultureInfo.InvariantCulture);
                }

                return new EnumFlagMask(UnsignedMask: unsignedMask, SignedMask: 0L);
            }

            var signedMask = 0L;
            foreach (var defined in Enum.GetValues(type))
            {
                signedMask |= Convert.ToInt64(defined, CultureInfo.InvariantCulture);
            }

            return new EnumFlagMask(UnsignedMask: null, SignedMask: signedMask);
        });
}

/// <summary>
/// flags 枚举的已定义位掩码：<see cref="UnsignedMask"/> 非 <c>null</c> 表示底层类型是 <c>ulong</c>。
/// </summary>
internal readonly struct EnumFlagMask
{
    public EnumFlagMask(ulong? UnsignedMask, long SignedMask)
    {
        this.UnsignedMask = UnsignedMask;
        this.SignedMask = SignedMask;
    }

    /// <summary>底层类型为 <c>ulong</c> 时的掩码；否则为 <c>null</c>。</summary>
    public ulong? UnsignedMask { get; }

    /// <summary>底层类型不是 <c>ulong</c> 时的掩码。</summary>
    public long SignedMask { get; }
}
