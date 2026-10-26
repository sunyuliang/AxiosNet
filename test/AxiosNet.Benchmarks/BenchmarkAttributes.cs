namespace AxiosNet.Benchmarks;

/// <summary>
/// 基准类所属的菜单分组。分组是<b>口径声明</b>而不是装饰：数字里含不含 TCP 往返决定了它能不能和
/// 另一组放在同一个结论里（README「七组基准」一节的边界）。
/// <para>
/// 写在基准类自己身上，而不是集中到一张清单：新加一个基准类只改它自己的文件，
/// 菜单在运行时反射发现 —— 没有「构建 / 发布时登记支持哪些基准」这一步，也就不存在漏登记。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class BenchmarkGroupAttribute(string name, string note) : Attribute
{
    public string Name { get; } = name;

    /// <summary>分组的口径说明，印在菜单的分组标题行上。</summary>
    public string Note { get; } = note;

    /// <summary>分组在菜单里的先后（小的在前）；同值按分组名排。</summary>
    public int Order { get; set; }
}

/// <summary>
/// 这组数字怎么读：比的是谁、看哪一列、哪一列不能当门槛。
/// <para>
/// 必须由人写 —— 反射拿不到「比值该乘还是该除」，所以它只能跟着基准类走；
/// 没写的类照样会出现在菜单里，只是读法处会提示补上，不会静默缺席。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class BenchmarkHintAttribute(string hint) : Attribute
{
    public string Hint { get; } = hint;
}
