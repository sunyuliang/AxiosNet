#if NETSTANDARD2_0
// 兼容包里带有几个 internal 的编译器特性类型，本程序集补一份同名私有版本；
// 按 C# 规则同程序集类型优先，这里抑制「与导入类型冲突」的 CS0436。
#pragma warning disable CS0436
// netstandard2.0 兼容资产的编译期补位：这里只补「编译器要认、但契约里没有」的类型，
// 不引入任何运行时行为。它们全是 internal，只在 AxiosNet 自己的编译期可见，
// 不会与 .NET Core 3.x / .NET 5+ 运行时自带的同名公共类型产生类型标识冲突。
// 真正影响行为的降级（传输层、版本协商、流式 override）分散在各自的源码文件里用 NETSTANDARD2_0 分支处理。
using System;

namespace System
{
    /// <summary>
    /// C# 8 范围语法需要的 <c>System.Index</c>：netstandard2.0 契约里没有，System.Memory 包也不带。
    /// 定义成库内私有类型，编译器优先用同程序集的定义，不会与 .NET Core 3.x 运行时自带的公共类型混淆。
    /// </summary>
    internal readonly struct Index
    {
        // 从末尾计数时把值取反存起来：0 与 ^0 必须是不同的两个值。
        private readonly int _value;

        /// <summary>按位置构造索引。</summary>
        /// <param name="value">位置（非负）。</param>
        /// <param name="fromEnd">是否从末尾倒数。</param>
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _value = fromEnd ? ~value : value;
        }

        /// <summary>索引的绝对值。</summary>
        public int Value => _value < 0 ? ~_value : _value;

        /// <summary>是否从末尾倒数。</summary>
        public bool IsFromEnd => _value < 0;

        /// <summary>在给定长度上换算成从开头数的偏移量。</summary>
        /// <param name="length">序列长度。</param>
        /// <returns>从开头数的偏移量。</returns>
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        /// <summary>序列起点。</summary>
        public static Index Start => new Index(0);

        /// <summary>序列末尾。</summary>
        public static Index End => new Index(0, fromEnd: true);

        /// <summary>从 int 隐式构造从开头数的索引。</summary>
        /// <param name="value">位置。</param>
        public static implicit operator Index(int value) => new Index(value);
    }

    /// <summary>C# 8 范围语法需要的 <c>System.Range</c>（同 <see cref="Index"/>）。</summary>
    internal readonly struct Range
    {
        /// <summary>范围起点。</summary>
        public Index Start { get; }

        /// <summary>范围终点（不含）。</summary>
        public Index End { get; }

        /// <summary>按起止索引构造范围。</summary>
        /// <param name="start">起点。</param>
        /// <param name="end">终点。</param>
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        /// <summary>从起点到末尾。</summary>
        /// <param name="start">起点。</param>
        /// <returns>范围。</returns>
        public static Range StartAt(Index start) => new Range(start, Index.End);

        /// <summary>从开头到终点。</summary>
        /// <param name="end">终点。</param>
        /// <returns>范围。</returns>
        public static Range EndAt(Index end) => new Range(Index.Start, end);

        /// <summary>整个序列。</summary>
        public static Range All => new Range(Index.Start, Index.End);

        /// <summary>在给定长度上换算出偏移与长度。</summary>
        /// <param name="length">序列长度。</param>
        /// <returns>偏移量与长度。</returns>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            return (start, end - start);
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// C# 9 的 init 访问器要求运行时存在本类型；netstandard2.0 契约里没有它。
    /// 只作为编译期标记，JIT 不会读取它的任何成员。
    /// </summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>
    /// 模块初始化器标记（WebAPI 的 FormatRegistration 用它注册格式化读取器）。
    /// Roslyn 会把带本标记的方法编进 &lt;Module&gt;.cctor，CLR 从 .NET Framework 1.0 起就执行它。
    /// </summary>
    /// <remarks>
    /// 必须保持 <c>internal</c>：一旦公开，引用本兼容资产的 net5+ 编译里
    /// <c>System.Runtime</c> 与 AxiosNet 会各有一份同名公共类型，生成代码绑定该名字时直接 CS0433。
    /// 消费方的生成代码由源生成器按需补一份同名 <c>internal</c> 声明（见 <c>SourceGen</c> 的模块初始化器补位）。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }

    /// <summary>让编译器把调用处实参的表达式文本填进可选参数（ThrowIfNull 的参数名靠它）。</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        /// <summary>声明要取哪一个参数的调用处表达式。</summary>
        /// <param name="parameterName">参数名。</param>
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        /// <summary>参数名。</summary>
        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// 裁剪 / AOT 元数据标注的成员种类位标志。
    /// netstandard2.0 的引用程序集把这些标注做成 internal，库无法访问；这里补一份库内私有版本。
    /// 兼容资产不跑裁剪分析器，这些标注只保留源码一致性，没有运行时语义。
    /// </summary>
    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        /// <summary>不保留任何成员。</summary>
        None = 0,

        /// <summary>公共无参构造。</summary>
        PublicParameterlessConstructor = 0x1,

        /// <summary>公共构造。</summary>
        PublicConstructors = 0x2 | PublicParameterlessConstructor,

        /// <summary>非公共构造。</summary>
        NonPublicConstructors = 0x4,

        /// <summary>公共方法。</summary>
        PublicMethods = 0x8,

        /// <summary>非公共方法。</summary>
        NonPublicMethods = 0x10,

        /// <summary>公共字段。</summary>
        PublicFields = 0x20,

        /// <summary>非公共字段。</summary>
        NonPublicFields = 0x40,

        /// <summary>公共嵌套类型。</summary>
        PublicNestedTypes = 0x80,

        /// <summary>非公共嵌套类型。</summary>
        NonPublicNestedTypes = 0x100,

        /// <summary>公共属性。</summary>
        PublicProperties = 0x200,

        /// <summary>非公共属性。</summary>
        NonPublicProperties = 0x400,

        /// <summary>公共事件。</summary>
        PublicEvents = 0x800,

        /// <summary>非公共事件。</summary>
        NonPublicEvents = 0x1000,

        /// <summary>接口实现映射。</summary>
        Interfaces = 0x2000,

        /// <summary>全部成员。</summary>
        All = ~None
    }

    /// <summary>标注反射会访问某类型的哪些成员（兼容资产上的空标注，仅用于源码统一）。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
                    AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method |
                    AttributeTargets.Class, Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        /// <summary>按位标志声明需要保留的成员种类。</summary>
        /// <param name="memberTypes">成员种类位标志。</param>
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
            => MemberTypes = memberTypes;

        /// <summary>需要保留的成员种类。</summary>
        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    /// <summary>无条件抑制裁剪 / AOT 分析器告警（兼容资产不跑分析器，保留它只为源码统一）。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class UnconditionalSuppressMessageAttribute : Attribute
    {
        /// <summary>声明抑制来源。</summary>
        /// <param name="category">告警类别。</param>
        /// <param name="checkId">规则编号。</param>
        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
            Category = category;
            CheckId = checkId;
        }

        /// <summary>告警类别。</summary>
        public string Category { get; }

        /// <summary>规则编号。</summary>
        public string CheckId { get; }

        /// <summary>抑制范围。</summary>
        public string? Scope { get; set; }

        /// <summary>抑制目标。</summary>
        public string? Target { get; set; }

        /// <summary>消息编号。</summary>
        public string? MessageId { get; set; }

        /// <summary>抑制理由。</summary>
        public string? Justification { get; set; }
    }

    /// <summary>标注方法依赖未被静态引用的代码（兼容资产上的空标注）。</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        /// <summary>声明依赖未静态引用代码的说明。</summary>
        /// <param name="message">说明文本。</param>
        public RequiresUnreferencedCodeAttribute(string message) => Message = message;

        /// <summary>说明文本。</summary>
        public string Message { get; }

        /// <summary>补充文档地址。</summary>
        public string? Url { get; set; }
    }

    /// <summary>标注返回指定值时该参数不为 null（可空性分析用）。</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        /// <summary>声明非空条件。</summary>
        /// <param name="returnValue">使参数非空的返回值。</param>
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        /// <summary>使参数非空的返回值。</summary>
        public bool ReturnValue { get; }
    }
}
#endif
