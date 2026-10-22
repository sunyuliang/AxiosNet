namespace AxiosNet;

/// <summary>
/// <see cref="AxiosConfig.Params"/> 里集合值的编码形态（对应 axios 的 <c>arrayFormat</c> 与 Refit 的
/// <c>CollectionFormat</c>）。
/// <para>只影响<b>集合</b>；嵌套对象仍是 <c>a[b]=v</c>，null 元素仍然跳过。</para>
/// </summary>
public enum AxiosCollectionFormat
{
    /// <summary><c>a[]=1&amp;a[]=2</c>（默认，axios 默认形态）。</summary>
    Brackets = 0,

    /// <summary><c>a=1&amp;a=2</c>（重复键）。</summary>
    Multi = 1,

    /// <summary><c>a=1,2</c>（逗号分隔，整体作为一个值转义）。</summary>
    Csv = 2,

    /// <summary><c>a=1%202</c>（空格分隔：<c>"1 2"</c> 整体转义）。</summary>
    Ssv = 3,

    /// <summary><c>a=1%092</c>（制表符分隔：<c>"1\t2"</c> 整体转义）。</summary>
    Tsv = 4,

    /// <summary><c>a=1%7C2</c>（竖线分隔：<c>"1|2"</c> 整体转义）。</summary>
    Pipes = 5
}
