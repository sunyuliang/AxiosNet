using AxiosNet.Simple;          // SampleMenu / SampleMenuItem（链接自 AxiosNet.Simple/SampleMenu.cs）
using AxiosNet.Simple.WebAPI;   // DemoApiServer 与请求/返回模型（链接自 AxiosNet.Simple/WebAPI）
using AxiosNet.Simple.DependencyInjection.Examples;

// AxiosNet.Simple.DependencyInjection —— AxiosNet.DependencyInjection 扩展示例
// 启动时反射列出全部场景，输入编号运行，执行完毕回到菜单继续选择，直到输入 q 退出（a = 顺序运行全部）。
// 场景零注册：Scenarios/ 下实现 IScenario 的类自动进菜单，按自身 Number 排序。
// 每个场景各建一个 Host：同一容器内单例只能注册一次（TryAdd 先到先得），场景间互相隔离。
//   1) AddAxiosClient                   核心客户端单例
//   2) AddApiClient 自建模式            自带 BaseAddress 自建底层客户端
//   3) AddApiClient 门面模式            复用 AddAxiosClient 注册的单例
//   4) 容器授权                         IAuthorizationProvider 单例自动装配
//   5) AddApiClient(name, ...) 多站点   命名注册 + IApiClientResolver 按站点名取用
//   6) 键名服务（.NET 8+ 官方机制）      AddKeyedSingleton + [FromKeyedServices] / GetRequiredKeyedService
// 示例自带本地 HTTP/1.1 Keep-Alive 演示服务器，按首次选择懒启动、退出时释放，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple.DependencyInjection

var items = new List<SampleMenuItem>();
DemoApiServer? server = null;

foreach (var scenario in Discover(typeof(IExample))
    .Select(t => (IExample)Activator.CreateInstance(t)!)
    .OrderBy(s => s.Number))
{
    items.Add(new SampleMenuItem("DI 扩展示例", scenario.Number, scenario.GetType().Name, scenario.Title, () =>
    {
        var started = server ??= StartServer();
        return scenario.RunAsync(started.Url);
    }));
}

try
{
    var failed = await SampleMenu.RunAsync(
        $"AxiosNet.DependencyInjection 示例库（反射发现，共 {items.Count} 项）", items);

    if (failed)
    {
        Environment.ExitCode = 1;
    }
}
finally
{
    server?.Dispose();
}

// ------------------------------------------------------------------
// 辅助
// ------------------------------------------------------------------

// 反射发现实现 scenarioInterface 的全部具体场景类（抽象类与接口自身除外）
static Type[] Discover(Type scenarioInterface)
    => scenarioInterface.Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && scenarioInterface.IsAssignableFrom(t))
        .ToArray();

// 演示服务器懒启动：第一次真正执行场景时创建，顺带打印地址（与 AxiosNet.Simple 的懒启动一致）
static DemoApiServer StartServer()
{
    var started = DemoApiServer.Start();
    Console.WriteLine($"本地演示服务器: {started.Url}");
    return started;
}
