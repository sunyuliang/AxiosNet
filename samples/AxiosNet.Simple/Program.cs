using AxiosNet.Simple;
using AxiosNet.Simple.Examples;
using AxiosNet.Simple.WebAPI;
using AxiosNet.Simple.WebAPI.Examples;
using CoreExample = AxiosNet.Simple.Examples.IExample;
using CoreContext = AxiosNet.Simple.Examples.ExampleContext;
using WebApiExample = AxiosNet.Simple.WebAPI.Examples.IExample;
using WebApiContext = AxiosNet.Simple.WebAPI.Examples.ExampleContext;

// AxiosNet.Simple —— AxiosNet 各类集成调用示例
// 启动时反射列出全部示例，用户输入编号运行，执行完毕回到菜单继续选择，直到输入 q 退出（a = 顺序运行全部）。
// 示例零注册：Examples/ 与 WebAPI/Examples/ 下实现 IExample 的类自动进菜单，按自身 Number 排序。
// 两套示例各带本地 HTTP/1.1 Keep-Alive 演示服务器，按首次选择懒启动，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple

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
    var failed = await SampleMenu.RunAsync($"AxiosNet 示例库（反射发现，共 {items.Count} 项）", items);
    if (failed)
    {
        Environment.ExitCode = 1;
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

// 反射发现实现 exampleInterface 的全部具体示例类（抽象类与接口自身除外）
static Type[] Discover(Type exampleInterface)
    => exampleInterface.Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && exampleInterface.IsAssignableFrom(t))
        .ToArray();
