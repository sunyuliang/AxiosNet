using System.Collections.Concurrent;
using System.Globalization;

namespace AxiosNet.Internal;

/// <summary>
/// 运行期类型事实（按类型缓存）：flags 枚举的已定义位掩码、是否实现 <see cref="IAsyncEnumerable{T}"/>。
/// 只读类型自身的元数据、不读特性，Native AOT 下同样可用。
/// </summary>
internal static class TypeFacts
{
    /// <summary>flags 枚举的「已定义位掩码」（非 flags 枚举缓存 <c>null</c>）。</summary>
    private static readonly ConcurrentDictionary<Type, EnumFlagMask?> FlagMasks = new();

    /// <summary>类型是否实现 <c>IAsyncEnumerable&lt;T&gt;</c>（只用于错误提示）。</summary>
    private static readonly ConcurrentDictionary<Type, bool> AsyncEnumerableTypes = new();

    /// <summary>
    /// 类型是否实现了任意 <see cref="IAsyncEnumerable{T}"/>（按类型缓存），只用于错误提示。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
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
                foreach (var defined in GetValues(type))
                {
                    unsignedMask |= Convert.ToUInt64(defined, CultureInfo.InvariantCulture);
                }

                return new EnumFlagMask(UnsignedMask: unsignedMask, SignedMask: 0L);
            }

            var signedMask = 0L;
            foreach (var defined in GetValues(type))
            {
                signedMask |= Convert.ToInt64(defined, CultureInfo.InvariantCulture);
            }

            return new EnumFlagMask(UnsignedMask: null, SignedMask: signedMask);
        });

    /// <summary>枚举的全部定义值；net8 起用 AOT 安全的重载（按底层类型返回，不要求运行时代码生成）。</summary>
    private static Array GetValues(Type enumType)
    {
#if NET8_0_OR_GREATER
        return Enum.GetValuesAsUnderlyingType(enumType);
#else
        return Enum.GetValues(enumType);
#endif
    }
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
