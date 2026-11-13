#if NETSTANDARD2_0
// netstandard2.0 兼容资产的集中补位：用全局别名把源码里的少数 BCL 类型名接到库内 shim，
// 避免把 #if 撒进上百个文件。别名只在这个资产上生效，net6.0 / net8.0 完全看不到本文件。
// 每个 shim 只复刻源码真正用到的那部分成员，行为与 .NET 6+ 的对应 API 对齐。
global using ArgumentNullException = AxiosNet.Compatibility.ArgumentNullExceptionCompat;
global using RuntimeFeature = AxiosNet.Compatibility.RuntimeFeatureCompat;
global using HashCode = AxiosNet.Compatibility.HashCodeCompat;
global using ReferenceEqualityComparer = AxiosNet.Compatibility.ReferenceEqualityComparerCompat;
global using ReadOnlyMemoryContent = AxiosNet.Compatibility.ReadOnlyMemoryContentCompat;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AxiosNet.Compatibility;

/// <summary>
/// <c>ArgumentNullException.ThrowIfNull</c> 的等价物：netstandard2.0 没有这个静态方法。
/// 继承真正的 <see cref="System.ArgumentNullException"/>，所以 <c>throw new</c> / 参数名 / 消息语义不变；
/// 兼容资产里没有 <c>catch (ArgumentNullException)</c> 的代码路径。
/// </summary>
internal sealed class ArgumentNullExceptionCompat : System.ArgumentNullException
{
    /// <summary>不带消息的空构造。</summary>
    public ArgumentNullExceptionCompat()
    {
    }

    /// <summary>按参数名构造。</summary>
    /// <param name="paramName">为 null 的参数名。</param>
    public ArgumentNullExceptionCompat(string? paramName) : base(paramName)
    {
    }

    /// <summary>按参数名与消息构造。</summary>
    /// <param name="paramName">为 null 的参数名。</param>
    /// <param name="message">异常消息。</param>
    public ArgumentNullExceptionCompat(string? paramName, string? message) : base(paramName, message)
    {
    }

    /// <summary>按消息与内部异常构造。</summary>
    /// <param name="message">异常消息。</param>
    /// <param name="innerException">内部异常。</param>
    public ArgumentNullExceptionCompat(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>参数为 null 时抛 <see cref="System.ArgumentNullException"/>；参数名由编译器自动填入。</summary>
    /// <param name="argument">要检查的值。</param>
    /// <param name="paramName">调用处表达式文本（由 CallerArgumentExpression 提供）。</param>
    public static void ThrowIfNull(object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new System.ArgumentNullException(paramName);
        }
    }
}

/// <summary>
/// <c>RuntimeFeature.IsDynamicCodeSupported</c> 的等价物。兼容资产只跑在 CLR / CoreCLR 的 JIT 上，
/// 不存在 Native AOT，因此恒为 true；库内的 RuntimeSupport.ForceNoDynamicCode 测试开关仍然生效。
/// </summary>
internal static class RuntimeFeatureCompat
{
    /// <summary>兼容资产始终支持动态代码生成。</summary>
    public static bool IsDynamicCodeSupported => true;
}

/// <summary><c>HashCode.Combine</c> 的等价物：源码只用四参重载，按稳定顺序混合四个哈希。</summary>
internal static class HashCodeCompat
{
    /// <summary>把四个值的哈希混成一个；同一组值必得同一结果。</summary>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    /// <param name="value3">第三个值。</param>
    /// <param name="value4">第四个值。</param>
    /// <returns>组合后的哈希码。</returns>
    public static int Combine<T1, T2, T3, T4>(T1 value1, T2 value2, T3 value3, T4 value4)
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + (value1?.GetHashCode() ?? 0);
            hash = (hash * 31) + (value2?.GetHashCode() ?? 0);
            hash = (hash * 31) + (value3?.GetHashCode() ?? 0);
            hash = (hash * 31) + (value4?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

/// <summary><c>ReferenceEqualityComparer.Instance</c> 的等价物：按引用判等、按对象标识取哈希。</summary>
internal static class ReferenceEqualityComparerCompat
{
    /// <summary>共享的按引用比较器；借用 IEqualityComparer 的逆变性，可直接用于 HashSet&lt;Stream&gt; 等。</summary>
    public static IEqualityComparer<object> Instance { get; } = new Impl();

    /// <summary>按引用比较的实现。</summary>
    private sealed class Impl : IEqualityComparer<object>
    {
        /// <summary>引用相等判定。</summary>
        /// <param name="x">左值。</param>
        /// <param name="y">右值。</param>
        /// <returns>是否同一实例。</returns>
        // new 是刻意的：object.Equals(object, object) 是静态成员，实例方法与之同名会被判为隐藏。
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        /// <summary>对象标识哈希。</summary>
        /// <param name="obj">目标对象。</param>
        /// <returns>基于对象标识的哈希码。</returns>
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// <c>ReadOnlyMemoryContent</c> 的等价物：netstandard2.0 的 System.Net.Http 没有这个类型。
/// 这里保留同样的「不拥有内存、只按引用持有」契约，写出时多一次数组拷贝（兼容资产的已知性能降级）。
/// </summary>
internal sealed class ReadOnlyMemoryContentCompat : HttpContent
{
    // 由调用方持有生命周期；本内容不释放它。
    private readonly ReadOnlyMemory<byte> _content;

    /// <summary>用一段只读字节内存构造正文。</summary>
    /// <param name="content">正文内存；生命周期必须覆盖整条请求。</param>
    public ReadOnlyMemoryContentCompat(ReadOnlyMemory<byte> content) => _content = content;

    /// <summary>写出正文；netstandard2.0 的 HttpContent 只有无令牌的抽象重载。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="context">传输上下文（未使用）。</param>
    /// <returns>写出任务。</returns>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var bytes = _content.ToArray();
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    /// <summary>长度已知，直接声明 Content-Length。</summary>
    /// <param name="length">正文长度。</param>
    /// <returns>总是 true。</returns>
    protected override bool TryComputeLength(out long length)
    {
        length = _content.Length;
        return true;
    }
}
#endif
