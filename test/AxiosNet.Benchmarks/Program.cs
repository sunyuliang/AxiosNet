using AxiosNet.Benchmarks.Harness;
using BenchmarkDotNet.Running;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 基准入口。两条通道，互不干扰：
/// <list type="bullet">
/// <item><b>带参数</b> —— 原样交给 <see cref="BenchmarkSwitcher"/>（<c>--filter</c> / <c>--list</c> /
/// <c>--job</c> / <c>--exporters</c> 都是 BDN 自己的参数）。脚本、README 的「前后各跑一轮」流程走这条；</item>
/// <item><b>不带参数</b> —— 交互终端进菜单（选组 → 跑 → 看摘要 → 回到菜单），
/// 形态与 <c>samples/AxiosNet.Simple</c> 一致；标准输入被重定向时退回打印用法，绝不进菜单
/// —— 菜单会阻塞在 <c>ReadLine</c> 上，CI / 管道里那就是挂住。</item>
/// </list>
/// <para>
/// 用法见同目录 README.md：<c>dotnet run -c Release</c>（菜单）或
/// <c>dotnet run -c Release -- --filter '*Merge*'</c>（单组）。
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
#if DEBUG
        Console.Error.WriteLine(
            "[注意] 当前是 Debug 构建：被测库（AxiosNet）也是未优化的 IL，同进程模式下数字只能用来确认"
            + "「跑得通」，不能当基线 —— 要数字请用 dotnet run -c Release。");
        Console.Error.WriteLine();
#endif

        if (args.Length > 0)
        {
            return BenchmarkLauncher.HasFailures(BenchmarkLauncher.Run(args)) ? 1 : 0;
        }

        if (Console.IsInputRedirected)
        {
            PrintUsage();
            return 0;
        }

        return BenchmarkMenu.Run();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AxiosNet 性能基准（BenchmarkDotNet）");
        Console.WriteLine();
        Console.WriteLine("  dotnet run -c Release                              交互菜单：选组运行，跑完打印摘要");
        // filter 一定要加引号：不加的话 bash 会先把 * 展开成当前目录的文件名，BDN 收到一串路径后报
        // 「Filters 'AxiosNet.sln', 'LICENSE' ... returned 0 benchmarks」。
        Console.WriteLine("  dotnet run -c Release -- --filter '*Merge*'        只跑名字含 Merge 的基准（不提问，适合脚本）");
        Console.WriteLine("  dotnet run -c Release -- --list flat               列出全部基准（不运行）");
        Console.WriteLine("  dotnet run -c Release -- --filter '*' --job short  快速冒烟（迭代少、误差大，不能当基线）");
        Console.WriteLine("  dotnet run -c Release -- --filter '*' --exporters json");
        Console.WriteLine();
        Console.WriteLine("执行方式（默认同进程，加 --isolated 切换）：");
        Console.WriteLine("  同进程  不生成临时项目、不重建、不另起进程，跑的就是当前这个程序集（Release 就 Release，Debug 就 Debug）");
        Console.WriteLine("  隔离    为作业生成并编译临时项目，每个用例一个独立子进程；要写进提交信息 / 与历史基线比时用它");
        Console.WriteLine();
        Console.WriteLine("可用的基准类（每组对应一个热点，菜单里带读法说明）：");
        Console.WriteLine();

        var nameWidth = BenchmarkDiscovery.Entries.Max(entry => entry.Name.Length);
        foreach (var entry in BenchmarkDiscovery.Entries)
        {
            Console.WriteLine($"  {entry.Name.PadRight(nameWidth)}  {entry.Title}");
        }
    }
}
