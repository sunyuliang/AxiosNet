using AxiosNet.Simple;
using AxiosNet.Simple.WebAPI;
using AxiosNet.Simple.WebAPI.Examples;
using CoreExample = AxiosNet.Simple.Examples.IExample;
using CoreContext = AxiosNet.Simple.ExampleContext;
using WebApiExample = AxiosNet.Simple.WebAPI.Examples.IExample;
using WebApiContext = AxiosNet.Simple.WebAPI.Examples.ExampleContext;

// AxiosNet.Simple —— AxiosNet 各类集成调用示例
// 不带参数启动时反射列出全部示例，用户输入编号运行，执行完毕回到菜单继续选择，直到输入 q 退出（a = 顺序运行全部）。
// 带参数启动时直接点名运行，不进菜单：--list 列出菜单；数字 = 菜单编号；其它 = 类名（大小写不敏感）。
// 示例零注册：Examples/ 与 WebAPI/Examples/ 下实现 IExample 的类自动进菜单，按自身 Number 排序。
// 两套示例各带本地 HTTP/1.1 Keep-Alive 演示服务器，按首次选择懒启动，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple
//   dotnet run --project samples/AxiosNet.Simple -- --list
//   dotnet run --project samples/AxiosNet.Simple -- RetryExample TypedApiExample

var items = new List<SampleMenuItem>();

DemoServer? coreServer = null;
DemoApiServer? webApiServer = null;

// 核心套件：Examples/ 目录
foreach (var example in Discover(typeof(CoreExample))
    .Select(t => (CoreExample)Activator.CreateInstance(t)!)
    .OrderBy(e => e.Number))
{
    items.Add(new SampleMenuItem("核心集成示例", example.Number, example.GetType().Name, example.Title, () =>
    {
        var server = coreServer ??= DemoServer.Start();
        return example.RunAsync(new CoreContext(server.Url, server));
    }));
}

// 声明式套件：WebAPI/Examples/ 目录（自带另一套演示服务器）
foreach (var example in Discover(typeof(WebApiExample))
    .Select(t => (WebApiExample)Activator.CreateInstance(t)!)
    .OrderBy(e => e.Number))
{
    items.Add(new SampleMenuItem("声明式 API 客户端示例", example.Number, example.GetType().Name, example.Title, () =>
    {
        var server = webApiServer ??= DemoApiServer.Start();
        return example.RunAsync(new WebApiContext(server.Url, server));
    }));
}

try
{
    if (args.Length > 0)
    {
        // 直接点名运行：CI / 手查单个能力时不进菜单。
        if (!await RunNamedAsync(args, items))
        {
            Environment.ExitCode = 1;
        }
    }
    else
    {
        var failed = await SampleMenu.RunAsync($"AxiosNet 示例库（反射发现，共 {items.Count} 项）", items);
        if (failed)
        {
            Environment.ExitCode = 1;
        }
    }
}
finally
{
    coreServer?.Dispose();
    webApiServer?.Dispose();
}

// ------------------------------------------------------------------
// 辅助
// ------------------------------------------------------------------

// 按命令行参数运行示例：--list 只列菜单；其余每项是菜单编号或类名（大小写不敏感）。
// 多个参数按顺序依次运行，任一失败即返回 false（其余照跑，便于一次看全）。
static async Task<bool> RunNamedAsync(string[] args, IReadOnlyList<SampleMenuItem> items)
{
    if (args.Length == 1 && args[0] is "--list" or "-l" or "list")
    {
        SampleMenu.Print(items);
        return true;
    }

    var ok = true;
    foreach (var arg in args)
    {
        var index = Resolve(arg, items);
        if (index < 0)
        {
            Console.WriteLine($"未找到示例「{arg}」：可用编号 1-{items.Count} 或类名（大小写不敏感）；加 --list 查看全部。");
            ok = false;
            continue;
        }

        ok &= await SampleMenu.RunItemAsync(index, items[index - 1]);
    }

    return ok;
}

// 解析成菜单下标（1 起）；解析不到返回 -1。先认菜单编号，再认类名。
// 这里刻意不按 IExample.Number 匹配：两套示例的编号分别是 1-39 与 1-17，全落在菜单编号
// 1-56 之内，数字永远被第一段吃掉（声明式套件的 Number 5 与菜单第 5 项不是同一项），
// 所以「按示例自己声明的编号运行」不是一项能力，别在文档里承诺它。
static int Resolve(string arg, IReadOnlyList<SampleMenuItem> items)
{
    if (int.TryParse(arg, out var choice) && choice >= 1 && choice <= items.Count)
    {
        return choice;
    }

    for (var i = 0; i < items.Count; i++)
    {
        if (string.Equals(items[i].Name, arg, StringComparison.OrdinalIgnoreCase))
        {
            return i + 1;
        }
    }

    return -1;
}

// 反射发现实现 exampleInterface 的全部具体示例类（抽象类与接口自身除外）
static Type[] Discover(Type exampleInterface)
    => exampleInterface.Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && exampleInterface.IsAssignableFrom(t))
        .ToArray();
