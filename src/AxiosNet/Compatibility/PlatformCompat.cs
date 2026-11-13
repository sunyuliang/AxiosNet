using System;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AxiosNet;

/// <summary>
/// 三目标共用的 BCL 小能力入口：源码统一调用这里，具体实现对 netstandard2.0 兼容资产做等价降级。
/// 这样业务代码不需要为「某 API 只在 net6+ 存在」到处写 #if，行为差异集中在一处可审计。
/// </summary>
internal static class PlatformCompat
{
    /// <summary>数组的最大长度（与 BCL 的 <c>Array.MaxLength</c> 同值）。</summary>
    internal const int ArrayMaxLength = 0x7FFFFFC7;

    /// <summary>PATCH 方法：netstandard2.0 的 HttpMethod 没有 Patch 静态属性。</summary>
    internal static readonly HttpMethod PatchMethod =
#if NETSTANDARD2_0
        new HttpMethod("PATCH");
#else
        HttpMethod.Patch;
#endif

    /// <summary>ISO-8859-1（Latin-1）编码：netstandard2.0 没有 Encoding.Latin1 单例。</summary>
    internal static readonly Encoding Latin1 =
#if NETSTANDARD2_0
        Encoding.GetEncoding(28591);
#else
        Encoding.Latin1;
#endif

    // 兼容资产没有 Random.Shared；用一把小锁保证多线程下 NextDouble 不退化。
    private static readonly Random JitterRandomSource = new Random();
    private static readonly object JitterRandomGate = new object();

    /// <summary>取 [0,1) 的抖动随机数（线程安全，语义对齐 Random.Shared.NextDouble）。</summary>
    /// <returns>随机小数。</returns>
    internal static double NextJitter()
    {
#if NETSTANDARD2_0
        lock (JitterRandomGate)
        {
            return JitterRandomSource.NextDouble();
        }
#else
        return Random.Shared.NextDouble();
#endif
    }

    /// <summary>单调毫秒计数；netstandard2.0 没有 Environment.TickCount64，用高精度计时器换算。</summary>
    /// <returns>系统开机以来的毫秒数（不回绕；与 Environment.TickCount64 同口径）。</returns>
    internal static long TickCount64()
    {
#if NETSTANDARD2_0
        // 不能写「GetTimestamp() * 1000L / Frequency」：先乘后除会溢出。
        // Linux / macOS 的 Stopwatch.Frequency 恒为 10^9（clock_gettime 纳秒口径，Windows QPC 才是 10^7），
        // 而 CLOCK_MONOTONIC 从开机起算，开机约 106.8 天后 ts * 1000L 就超过 long.MaxValue，回绕成错误值，
        // 会让 MemoryCacheStore 的 TTL 判定整体失真。改用 double 常数因子：ts 上限约 3e19 ns，
        // double 相对误差 2^-52，换算到毫秒误差远小于 1µs，且任意开机时长都不会溢出。
        return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
#else
        return Environment.TickCount64;
#endif
    }

    /// <summary>浮点是否有限（非 NaN、非无穷）。</summary>
    /// <param name="value">待判定的值。</param>
    /// <returns>是否有限。</returns>
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>从结果构造一个已完成的 ValueTask（netstandard2.0 没有 ValueTask.FromResult）。</summary>
    /// <param name="value">结果值。</param>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <returns>已完成的 ValueTask。</returns>
    internal static ValueTask<TResult> FromResult<TResult>(TResult value) => new ValueTask<TResult>(value);

    /// <summary>类型是否为 byref-like（Span 等只能活在栈上的类型）。</summary>
    /// <param name="type">待判定的类型。</param>
    /// <returns>是否 byref-like。</returns>
    internal static bool IsByRefLike(Type type)
    {
#if NETSTANDARD2_0
        // netstandard2.0 的 Type 契约里没有该属性：运行时若自带（.NET Core 3.x+）就反射取值；
        // .NET Framework 4.7.2+ 没有该属性，退到元数据上的 [IsByRefLike] 标记。
        // 这里的检查按成员逐个调用（ObjectShapeReflection / InterfaceDeclaration）：把属性 getter 直接绑定成
        // 强类型委托只做一次，之后是普通委托调用；PropertyInfo.GetValue 每次都会装箱返回值且走反射调度。
        if (ByRefLikeGetter is { } getter)
        {
            return getter(type);
        }

        if (ByRefLikeProperty is { } property)
        {
            return property.GetValue(type) is bool value && value;
        }

        // .NET Framework 4.7.2+：Type 没有 IsByRefLike 属性，ref struct 只能靠元数据上的
        // [IsByRefLike] 标记识别。不能按 Type 身份匹配——System.Memory 等程序集在编译期
        // 内嵌了自己的 IsByRefLikeAttribute 副本，mscorlib 的同名特性 IsDefined 实测返回 false，
        // 因此按完整类型名匹配（读 CustomAttributeData，不实例化特性对象）。
        foreach (var attribute in CustomAttributeData.GetCustomAttributes(type))
        {
            if (attribute.AttributeType.FullName == IsByRefLikeAttributeFullName)
            {
                return true;
            }
        }

        return false;
#else
        return type.IsByRefLike;
#endif
    }

#if NETSTANDARD2_0
    // 编译期内嵌的 [IsByRefLike] 可能与 mscorlib 里的同名特性属于不同程序集，只能按名字比对。
    private const string IsByRefLikeAttributeFullName = "System.Runtime.CompilerServices.IsByRefLikeAttribute";

    private static readonly PropertyInfo? ByRefLikeProperty =
        typeof(Type).GetProperty("IsByRefLike", BindingFlags.Public | BindingFlags.Instance);

    private static readonly Func<Type, bool>? ByRefLikeGetter = CreateByRefLikeGetter();

    private static Func<Type, bool>? CreateByRefLikeGetter()
    {
        var getter = ByRefLikeProperty?.GetGetMethod();
        if (getter is null)
        {
            return null;
        }

        try
        {
            // 开放实例方法：第一个参数就是 Type（即属性 getter 的 this）。
            return (Func<Type, bool>)getter.CreateDelegate(typeof(Func<Type, bool>));
        }
        catch (ArgumentException)
        {
            // 个别运行时不允许这种绑定：保留 PropertyInfo 兜底。
            return null;
        }
    }
#endif

    /// <summary>把字节区间转 base64（netstandard2.0 的 Convert 只接受数组）。</summary>
    /// <param name="bytes">源字节。</param>
    /// <returns>base64 字符串。</returns>
    internal static string ToBase64String(ReadOnlySpan<byte> bytes)
    {
#if NETSTANDARD2_0
        return Convert.ToBase64String(bytes.ToArray());
#else
        return Convert.ToBase64String(bytes);
#endif
    }

    /// <summary>把字符串散成 8 字节 SHA-256 的大写十六进制前缀，用作缓存键。</summary>
    /// <param name="value">原始字符串。</param>
    /// <returns>16 个大写十六进制字符。</returns>
    internal static string Sha256HexPrefix8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
#if NETSTANDARD2_0
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
        }
#else
        return Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8));
#endif
    }
}
