using System.ComponentModel;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace AxiosNet.Benchmarks.Harness;

/// <summary>
/// 菜单条目：一个基准类 = 一个热点。标题、分组、读法全部来自类上的特性
/// （<see cref="DescriptionAttribute"/> / <see cref="BenchmarkGroupAttribute"/> / <see cref="BenchmarkHintAttribute"/>）。
/// </summary>
internal sealed class BenchmarkEntry(Type type)
{
    /// <summary>基准类的运行时类型；标题、分组、读法都从它身上的特性取。</summary>
    public Type Type { get; } = type;

    /// <summary>基准类名，与源文件名一一对应，便于按菜单项定位代码。</summary>
    public string Name => Type.Name;

    /// <summary>菜单里的一句话说明，取自类上的 <see cref="DescriptionAttribute"/>；没写就是空串。</summary>
    public string Title { get; } = type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

    /// <summary>这组数字怎么读；没写特性的类给一句提示而不是留空。</summary>
    public string Hint { get; } =
        type.GetCustomAttribute<BenchmarkHintAttribute>()?.Hint
        ?? "（未写读法：在本类上加 [BenchmarkHint(\"比的是谁、看哪一列\")]）";
}

/// <summary>菜单分组：口径相同的基准放一起，顺序由 <see cref="BenchmarkGroupAttribute.Order"/> 决定。</summary>
internal sealed class BenchmarkGroup(string name, string note, BenchmarkEntry[] entries)
{
    /// <summary>分组名，与 <see cref="BenchmarkGroupAttribute"/> 的第一个参数同源。</summary>
    public string Name { get; } = name;

    /// <summary>分组的口径说明，印在分组标题行上。</summary>
    public string Note { get; } = note;

    /// <summary>该分组下的基准类（按类名字典序）；菜单编号就是摊平后的数组下标 + 1。</summary>
    public BenchmarkEntry[] Entries { get; } = entries;
}

/// <summary>
/// 基准目录：<b>运行时反射发现</b>，不维护任何清单。
/// <para>
/// 判定标准只有一条：类里有 <c>[Benchmark]</c> 方法。因此新加一个基准类不需要改这里、
/// 也不需要改 csproj —— 它编译进程序集的那一刻就已经在菜单里了（这正是「要跑哪些由运行时自己选」）。
/// 缺 <see cref="BenchmarkGroupAttribute"/> 的类不会被藏起来，而是落进「未分组」并在菜单里显示，
/// 缺 <see cref="BenchmarkHintAttribute"/> 的类读法处会提示补上：漏写可见，不会静默消失。
/// </para>
/// </summary>
internal static class BenchmarkDiscovery
{
    private const string UngroupedName = "未分组（类上缺 [BenchmarkGroup]）";

    /// <summary>反射发现一次并缓存：分组按 Order 排序，同值按分组名排。</summary>
    public static BenchmarkGroup[] Groups { get; } = Discover();

    /// <summary>扁平化的菜单项：编号 = 下标 + 1，与菜单打印顺序一致。</summary>
    public static BenchmarkEntry[] Entries { get; } = Groups.SelectMany(group => group.Entries).ToArray();

    /// <summary>反射找出所有含 <c>[Benchmark]</c> 方法的类，按分组归并并排序。</summary>
    private static BenchmarkGroup[] Discover()
    {
        var types = typeof(BenchmarkDiscovery).Assembly.GetTypes()
            .Where(IsBenchmarkClass)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        return types
            .GroupBy(GroupName, StringComparer.Ordinal)
            .Select(group =>
            {
                // 同一分组的口径说明取任意一个声明（同组的类按约定写同样的文字）
                var meta = group.Select(GroupMeta).First();

                return new
                {
                    meta.Order,
                    Group = new BenchmarkGroup(
                        group.Key,
                        meta.Note,
                        group.Select(type => new BenchmarkEntry(type)).ToArray()),
                };
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Group.Name, StringComparer.Ordinal)
            .Select(item => item.Group)
            .ToArray();
    }

    /// <summary>「是基准类」的判据只认 <c>[Benchmark]</c>：不靠基类、不靠命名约定、不靠手工登记。</summary>
    private static bool IsBenchmarkClass(Type type)
        => type is { IsClass: true, IsAbstract: false }
            && type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null);

    /// <summary>取类的分组名；没声明 <c>[BenchmarkGroup]</c> 就归到「未分组」。</summary>
    private static string GroupName(Type type)
        => type.GetCustomAttribute<BenchmarkGroupAttribute>()?.Name ?? UngroupedName;

    private static (string Note, int Order) GroupMeta(Type type)
    {
        var attribute = type.GetCustomAttribute<BenchmarkGroupAttribute>();

        return attribute is null
            ? ("这些类没声明分组：口径未知，别和别的组放进同一个结论", int.MaxValue)
            : (attribute.Note, attribute.Order);
    }
}
