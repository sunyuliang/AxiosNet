#if NETSTANDARD2_0
// netstandard2.0 兼容包把裁剪 / 可空性标注做成 internal，库无法访问；这里补一份库内私有版本。
// 按 C# 规则同程序集类型优先，抑制「与导入类型冲突」的 CS0436。
// 兼容资产不跑裁剪分析器，这些标注只保留源码一致性，没有运行时语义。
#pragma warning disable CS0436
using System;

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// 裁剪 / AOT 元数据标注的成员种类位标志；数值与 BCL 保持一致，便于同一份源码在三份资产上编译。
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

    /// <summary>标注方法返回指定值时，该 <c>out</c> 参数可能为 null（可空性分析用）。</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class MaybeNullWhenAttribute : Attribute
    {
        /// <summary>声明可空条件。</summary>
        /// <param name="returnValue">使参数可能为 null 的返回值。</param>
        public MaybeNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        /// <summary>使参数可能为 null 的返回值。</summary>
        public bool ReturnValue { get; }
    }
}
#endif
