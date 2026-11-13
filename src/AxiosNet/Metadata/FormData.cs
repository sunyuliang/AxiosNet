namespace AxiosNet.Metadata;

/// <summary>单个表单部件的覆盖项：字段名是否显式指定、文件名与 Content-Type。</summary>
/// <param name="FileName">显式文件名；<c>null</c> 表示沿用值自带或装配默认。</param>
/// <param name="ContentType">显式部件 Content-Type；<c>null</c> 表示沿用值自带或装配默认。</param>
/// <param name="HasExplicitName">字段名是否由调用方显式指定（决定它是否压过值自带的名字）。</param>
public readonly record struct FormPartOverride(
    string? FileName,
    string? ContentType,
    bool HasExplicitName)
{
    /// <summary>是否声明了任何覆盖项。</summary>
    public bool Any => HasExplicitName || FileName is not null || ContentType is not null;
}

/// <summary>
/// 带部件覆盖与值格式化信息的表单值容器：键是字段名，值是原样的表单值（<see cref="FormFile"/> / 流 / 字节 / 标量 / 嵌套对象）。
/// </summary>
/// <remarks>
/// 内核只按中性的「每个值可带覆盖与格式串」能力消费它；谁填充这些信息由调用方决定。
/// 装配失败时调用方可以调 <see cref="DisposeOwned"/> 回收已取得的流与内容。
/// </remarks>
public sealed class FormData : Dictionary<string, object?>
{
    /// <summary>字段名 → 部件覆盖（只登记确实声明了覆盖的项）。</summary>
    public Dictionary<string, FormPartOverride> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>字段名 → 值格式化（只登记确实声明了格式串的项）。</summary>
    public Dictionary<string, ValueFormat> Formats { get; } = new(StringComparer.Ordinal);

    /// <summary>数据来源名称：装配期错误用它做上下文（普通字典没有这个名字）。</summary>
    /// <remarks>netstandard2.0 兼容资产上是普通 <c>set</c>：跨程序集的 <c>init</c> 在 .NET Framework 消费方无法写入。</remarks>
#if NETSTANDARD2_0
    public string? SourceName { get; set; }
#else
    public string? SourceName { get; init; }
#endif

    /// <summary>回收本容器里已取得的流与内容（装配中途失败时用），单个部件释放失败不影响其余项。</summary>
    public void DisposeOwned() => AxiosNet.Internal.MultipartOwnership.DisposeOwned(Values);

    /// <summary>回收一组表单值里已取得的流与内容（例如迭代中途失败时）；单个部件释放失败不影响其余项。</summary>
    public static void DisposeOwnedValues(IEnumerable<object?> values)
        => AxiosNet.Internal.MultipartOwnership.DisposeOwned(values);
}
