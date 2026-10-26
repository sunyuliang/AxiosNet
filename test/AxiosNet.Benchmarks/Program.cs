using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 基准入口：把命令行原样交给 <see cref="BenchmarkSwitcher"/>（<c>--filter</c> / <c>--list</c> /
/// <c>--job</c> / <c>--exporters</c> 都是 BDN 自己的参数）。
/// <para>
/// 不带参数时只打印用法与可用基准，不进 BDN 的交互选择菜单 —— 在非交互终端里那会直接挂住。
/// </para>
/// <para>
/// 用法见同目录 README.md：<c>dotnet run -c Release -- --filter *Merge*</c>。
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
#if DEBUG
        Console.Error.WriteLine(
            "[注意] 当前是 Debug 构建：本项目已强制 Optimize=true，数字仍然有效，"
            + "但 BDN 的构建配置校验与团队口径都按 Release 走 —— 请用 dotnet run -c Release。");
        Console.Error.WriteLine();
#endif

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var summaries = BenchmarkSwitcher
            .FromAssembly(Assembly.GetExecutingAssembly())
            .Run(args)
            .ToArray();

        // 让脚本能拿到失败信号：BDN 把校验错误挂在 Summary 上，但退出码默认恒为 0。
        return summaries.Any(summary => summary.HasCriticalValidationErrors) ? 1 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AxiosNet 性能基准（BenchmarkDotNet）");
        Console.WriteLine();
        // filter 一定要加引号：不加的话 bash 会先把 * 展开成当前目录的文件名，BDN 收到一串路径后报
        // 「Filters 'AxiosNet.sln', 'LICENSE' ... returned 0 benchmarks」。
        Console.WriteLine("  dotnet run -c Release -- --filter '*Merge*'        只跑名字含 Merge 的基准");
        Console.WriteLine("  dotnet run -c Release -- --list flat               列出全部基准（不运行）");
        Console.WriteLine("  dotnet run -c Release -- --filter '*' --job short  快速冒烟（迭代少、误差大，不能当基线）");
        Console.WriteLine("  dotnet run -c Release -- --filter '*' --exporters json");
        Console.WriteLine();
        Console.WriteLine("可用的基准类（每组对应一个热点）：");
        Console.WriteLine();

        var types = typeof(Program).Assembly
            .GetTypes()
            .Where(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null))
            .OrderBy(type => type.Name, StringComparer.Ordinal);

        foreach (var type in types)
        {
            Console.WriteLine($"  {type.Name,-36} {Describe(type)}");
        }
    }

    /// <summary>取类型上的 XML 注释首行（<c>&lt;summary&gt;</c>）—— 没有就留空，不猜。</summary>
    private static string Describe(Type type)
    {
        var summary = type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
        return summary ?? string.Empty;
    }
}
