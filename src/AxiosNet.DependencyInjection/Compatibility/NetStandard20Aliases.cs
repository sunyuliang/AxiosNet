#if NETSTANDARD2_0
// netstandard2.0 兼容资产的编译期补位：本包源码只用到 ArgumentNullException.ThrowIfNull 这一个 net6+ API，
// 用 global using 把类型名接到同程序集的 shim，7 处调用点与 XML 文档里的 cref 都不用改。
// 兼容包里也带 internal 的同名编译器特性；按 C# 规则同程序集类型优先，抑制「与导入类型冲突」的 CS0436。
#pragma warning disable CS0436
global using ArgumentNullException = AxiosNet.DependencyInjection.Compatibility.ArgumentNullExceptionCompat;

using System;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// 让 shim 的 ThrowIfNull 也能由编译器自动填入调用处表达式；netstandard2.0 契约里没有该特性，
    /// 兼容包中的同名类型是 internal，因此这里补一份库内私有版本（只在编译期被识别）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        /// <summary>声明要取哪一个参数的调用处表达式。</summary>
        /// <param name="parameterName">参数名。</param>
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        /// <summary>参数名。</summary>
        public string ParameterName { get; }
    }
}

namespace AxiosNet.DependencyInjection.Compatibility
{
    /// <summary>
    /// <c>ArgumentNullException.ThrowIfNull</c> 的等价物：继承真正的异常类型，
    /// 所以参数名、异常类型与 <c>catch (ArgumentNullException)</c> 的语义在三个资产上完全一致。
    /// </summary>
    internal sealed class ArgumentNullExceptionCompat : System.ArgumentNullException
    {
        /// <summary>参数为 null 时抛出；参数名由编译器自动填入。</summary>
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
}
#endif
