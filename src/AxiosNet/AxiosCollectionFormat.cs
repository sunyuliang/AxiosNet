namespace AxiosNet;

/// <summary>
/// <see cref="AxiosConfig.Params"/> 里集合值的编码形态（对应 axios 的 <c>arrayFormat</c>）。
/// 只影响集合；嵌套对象仍是 <c>a[b]=v</c>，null 元素仍然跳过。
/// </summary>
public enum AxiosCollectionFormat
{
    /// <summary><c>a[]=1&amp;a[]=2</c>（默认）。</summary>
    Brackets = 0,

    /// <summary><c>a=1&amp;a=2</c>（重复键）。</summary>
    Multi = 1,

    /// <summary><c>a=1,2</c>（逗号分隔，整体作为一个值转义）。</summary>
    Csv = 2,

    /// <summary><c>a=1%202</c>（空格分隔）。</summary>
    Ssv = 3,

    /// <summary><c>a=1%092</c>（制表符分隔）。</summary>
    Tsv = 4,

    /// <summary><c>a=1%7C2</c>（竖线分隔）。</summary>
    Pipes = 5
}
