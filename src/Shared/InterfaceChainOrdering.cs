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

    /// <summary>数组实参的排序后缀：一维写 <c>[]</c>，多维按秩写 <c>[,]</c>。</summary>
    /// <param name="rank">数组维数（1 起）。</param>
    /// <remarks>两路径必须产出同形后缀：都按数组的秩数拼，不能一律写 <c>[]</c>，否则 <c>T[]</c> 与 <c>T[,]</c> 会算出同一个键。</remarks>
    public static string ArraySuffix(int rank)
        => rank <= 1 ? "[]" : "[" + new string(',', rank - 1) + "]";

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
