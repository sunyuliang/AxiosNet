namespace AxiosNet.Benchmarks.Harness;

/// <summary>
/// 交互式基准菜单：启动时列出全部组（运行时反射发现，见 <see cref="BenchmarkDiscovery"/>），
/// 输入编号跑一组，跑完回到菜单，直到输入 q 退出 —— 形态与 <c>samples/AxiosNet.Simple/SampleMenu.cs</c> 一致。
/// <para>
/// <b>为什么要有菜单</b>：基准的入口成本全在「记参数」上（<c>--filter '*Merge*'</c> 要记得类名、
/// <c>--job short</c> 要记得它只能冒烟）。菜单把「选哪一组」和「这组怎么读」放在一起，
/// 敲一个数字就能跑；命令行通道原样保留，脚本与 README 的前后对比流程不受影响。
/// </para>
/// <para>
/// <b>不自动清屏</b>：一轮输出动辄几十行，清屏会把上一组的结果冲掉，而前后两组放在同一屏对比才有意义。
/// </para>
/// </summary>
internal static class BenchmarkMenu
{
    /// <summary>运行菜单主循环；返回进程退出码（有组失败则 1）。</summary>
    public static int Run()
    {
        var entries = BenchmarkDiscovery.Entries;

        // 执行方式：同进程（默认）不重建、不另起进程，跑的就是当前这个程序集；
        // 隔离模式才会为作业生成临时项目、每个用例一个子进程。
        var inProcess = true;

        Console.WriteLine("AxiosNet 性能基准（BenchmarkDotNet）");
        Console.WriteLine($"默认作业 3 预热 + 8 迭代；全部 {entries.Length} 组一轮本机约 8~9 分钟。");
        Console.WriteLine($"产物目录：{Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts")}");
        Console.WriteLine(BenchmarkLauncher.DescribeToolchain(inProcess));

        var failed = false;
        var showMenu = true;

        while (true)
        {
            // 只在「该重新看一遍」时印菜单：跑完一组后菜单已经被输出顶走了，要重印；
            // 输错命令时刚看过，重印 18 行只会把刚才的提示刷掉。
            if (showMenu)
            {
                Print(entries);
                showMenu = false;
            }

            Console.WriteLine();
            Console.Write("输入编号运行，a = 顺序跑全部，s = 冒烟跑全部（--job short），m = 切换执行方式，q = 退出 > ");
            var line = Console.ReadLine();

            if (line is null)
            {
                break; // 标准输入结束（重定向 / 管道）：退出
            }

            var command = line.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (Is(command, "q", "quit", "exit"))
            {
                break;
            }

            if (Is(command, "m", "mode"))
            {
                inProcess = !inProcess;
                Console.WriteLine(BenchmarkLauncher.DescribeToolchain(inProcess));
                continue;
            }

            if (Is(command, "a", "all"))
            {
                // 不暂停：跑全部本来就是「放着让它跑」，中途停下来按键没有意义
                for (var i = 0; i < entries.Length; i++)
                {
                    failed |= RunOne(i + 1, entries[i], job: null, pause: false, inProcess);
                }

                showMenu = true;
                continue;
            }

            if (Is(command, "s", "short"))
            {
                for (var i = 0; i < entries.Length; i++)
                {
                    failed |= RunOne(i + 1, entries[i], job: "short", pause: false, inProcess);
                }

                showMenu = true;
                continue;
            }

            if (!int.TryParse(command, out var choice) || choice < 1 || choice > entries.Length)
            {
                Console.WriteLine($"无效输入「{command}」：有效范围 1-{entries.Length}，a = 全部，s = 冒烟，m = 执行方式，q = 退出");
                continue;
            }

            failed |= RunOne(choice, entries[choice - 1], job: null, pause: true, inProcess);
            showMenu = true;
        }

        return failed ? 1 : 0;
    }

    private static void Print(BenchmarkEntry[] entries)
    {
        // 类名均为 ASCII，按字符数填充即视觉等宽
        var nameWidth = 0;
        foreach (var entry in entries)
        {
            nameWidth = Math.Max(nameWidth, entry.Name.Length);
        }

        var index = 0;
        foreach (var group in BenchmarkDiscovery.Groups)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {group.Name} —— {group.Note} --");

            foreach (var entry in group.Entries)
            {
                index++;
                Console.WriteLine($"  [{index,2}] {entry.Name.PadRight(nameWidth)}  {entry.Title}");
                Console.WriteLine($"        读法：{entry.Hint}");
            }
        }
    }

    private static bool RunOne(int index, BenchmarkEntry entry, string? job, bool pause, bool inProcess)
    {
        Console.WriteLine();
        Console.WriteLine($"== [{index}] {entry.Name} — {entry.Title} ==");
        Console.WriteLine($"   读法：{entry.Hint}");
        Console.WriteLine();

        bool failed;
        try
        {
            failed = BenchmarkLauncher.HasFailures(BenchmarkLauncher.RunClass(entry.Name, job, inProcess));
        }
        catch (Exception ex)
        {
            // 单组失败不中断会话：报告后回到菜单继续选
            Console.WriteLine($"[基准执行失败] {entry.Name}: {ex.Message}");
            failed = true;
        }

        if (failed)
        {
            Console.WriteLine($"[基准未完成] {entry.Name}：见上面的构建 / 校验输出。");
        }

        if (pause)
        {
            Console.Write("按任意键继续");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }

        return failed;
    }

    private static bool Is(string command, params string[] options)
        => options.Any(option => string.Equals(command, option, StringComparison.OrdinalIgnoreCase));
}
