namespace AxiosNet.Simple;

/// <summary>
/// 菜单条目：组名（套件名）+ 套件内编号 + 类名 + 标题 + 执行委托（演示服务器与上下文由调用方捕获）。
/// </summary>
internal sealed class SampleMenuItem(string group, int number, string name, string title, Func<Task> run)
{
    public string Group { get; } = group;

    public int Number { get; } = number;

    /// <summary>示例类名（与源文件名一一对应，便于按菜单项定位代码）。</summary>
    public string Name { get; } = name;

    public string Title { get; } = title;

    public Func<Task> Run { get; } = run;
}

/// <summary>
/// 交互式示例菜单：启动时列出全部条目（由反射发现），输入编号执行单项，
/// 执行完毕回到菜单继续选择，直到输入 q 退出（标准输入结束同样退出）。
/// </summary>
internal static class SampleMenu
{
    /// <summary>运行菜单主循环；返回是否有条目执行失败（由调用方决定退出码）。</summary>
    public static async Task<bool> RunAsync(string title, IReadOnlyList<SampleMenuItem> items)
    {
        Console.WriteLine(title);

        var failed = false;
        while (true)
        {
            Print(items);

            Console.Write("输入编号运行，a = 顺序运行全部，q = 退出 > ");
            var line = Console.ReadLine();
           
            if (line is null)
            {
                break; // 标准输入结束（重定向 / 管道）：退出
            }

            // stdout 被重定向（管道 / CI / 重定向到文件）时 Console.Clear 会抛「句柄无效」：
            // 清屏只是交互体验，没有控制台就跳过。
            if (!Console.IsOutputRedirected)
            {
                Console.Clear();
            }

            var command = line.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (command is "q" or "Q" or "quit" or "exit")
            {
                break;
            }

            if (command is "a" or "A" or "all")
            {
                for (var i = 0; i < items.Count; i++)
                {
                    failed |= await RunItemAsync(i + 1, items[i]);
                }

                continue;
            }

            if (!int.TryParse(command, out var choice) || choice < 1 || choice > items.Count)
            {
                Console.WriteLine($"无效输入「{command}」：有效范围 1-{items.Count}，a = 全部，q = 退出");
                continue;
            }
           
            failed |= await RunItemAsync(choice, items[choice - 1]);
            Console.WriteLine($"{items[choice - 1].Name}执行完毕、按任意键继续");
            Console.ReadKey();
        }

        return failed;
    }

    private static void Print(IReadOnlyList<SampleMenuItem> items)
    {
        Console.WriteLine();

        // 类名占列对齐（类名均为 ASCII，按字符数填充即视觉等宽）
        var nameWidth = 0;
        foreach (var item in items)
        {
            nameWidth = Math.Max(nameWidth, item.Name.Length);
        }

        string? group = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!string.Equals(item.Group, group, StringComparison.Ordinal))
            {
                group = item.Group;
                Console.WriteLine();
                Console.WriteLine($"-- {group} --");
            }

            Console.WriteLine($"  [{i + 1,2}] {item.Name.PadRight(nameWidth)}  {item.Title}");
        }
    }

    private static async Task<bool> RunItemAsync(int index, SampleMenuItem item)
    {
        Console.WriteLine();
        Console.WriteLine($"== [{index}] {item.Name} — {item.Title} ==");

        try
        {
            await item.Run();
            Console.WriteLine($"== [{index}] 运行完毕 ==");
            return false;
        }
        catch (Exception ex)
        {
            // 单个示例失败不中断会话：报告后回到菜单继续选
            Console.WriteLine($"[示例执行失败] {item.Name}: {ex}");
            return true;
        }
    }
}
