using System.Collections.Generic;

namespace AxiosNet.Internal.Spec;

/// <summary>
/// 接口继承链的同层排序规则，反射期与源生成期共用同一份源文件：
/// 先按「依赖的接口数量」降序，再按 <see cref="SortKey"/> 的序数排序。
/// </summary>
/// <remarks>
/// 排序键必须用元数据名（<c>Ns.Outer+Inner`1</c>）而不是各自符号系统的显示名：
/// 生成器的 <c>global::Ns.IFoo&lt;string&gt;</c> 与反射的 <c>Ns.IFoo`1[[...]]</c> 在泛型接口上先后顺序不同
/// （<c>&lt;</c> 与 <c>`</c> 的码位相反），直接比较会让菱形继承里同名静态头 / 最近授权在两条路径上选中不同层。
/// </remarks>
internal static class InterfaceChainOrdering
{
    /// <summary>同层排序键：定义的元数据名，带泛型实参时追加 <c>|</c> 分隔的各实参元数据名。</summary>
    /// <param name="definitionName">接口定义的元数据名（已闭合类型取 <c>OriginalDefinition</c>）。</param>
    /// <param name="argumentNames">泛型实参的元数据名；非泛型传空集合。</param>
    public static string SortKey(string definitionName, IReadOnlyList<string> argumentNames)
    {
        if (argumentNames.Count == 0)
        {
            return definitionName;
        }

        var builder = new System.Text.StringBuilder(definitionName.Length + argumentNames.Count * 16);
        builder.Append(definitionName);
        for (var i = 0; i < argumentNames.Count; i++)
        {
            builder.Append('|').Append(argumentNames[i]);
        }

        return builder.ToString();
    }

    /// <summary>接口链排序比较：依赖接口数降序，同层再按 <see cref="SortKey"/> 序数升序。</summary>
    /// <param name="leftInheritedCount">左侧接口实现的接口数量。</param>
    /// <param name="leftKey">左侧接口的 <see cref="SortKey"/>。</param>
    /// <param name="rightInheritedCount">右侧接口实现的接口数量。</param>
    /// <param name="rightKey">右侧接口的 <see cref="SortKey"/>。</param>
    public static int Compare(int leftInheritedCount, string leftKey, int rightInheritedCount, string rightKey)
    {
        var byDepth = rightInheritedCount.CompareTo(leftInheritedCount);
        return byDepth != 0 ? byDepth : string.CompareOrdinal(leftKey, rightKey);
    }
}
