using AxiosNet.Simple.DependencyInjection.Scenarios;
using AxiosNet.Simple.Examples; // SampleMenu（链接自 AxiosNet.Simple/Examples）
using AxiosNet.Simple.WebAPI;   // DemoApiServer 与请求/返回模型（链接自 AxiosNet.Simple/WebAPI）

// AxiosNet.Simple.DependencyInjection —— AxiosNet.DependencyInjection 扩展示例
// 启动时反射列出全部场景，用户输入编号运行，执行完毕回到菜单继续选择，直到输入 q 退出（a = 顺序运行全部）。
// 场景零注册：Scenarios/ 目录下实现 IScenario 的类自动进菜单，按自身 Number 排序。
// 每个场景各建一个 Host：同一容器内单例只能注册一次（TryAdd 先到先得），场景间互相隔离。
//   1) AddAxiosClient                   核心客户端单例
//   2) AddApiClient 自建模式            自带 BaseAddress 自建底层客户端
//   3) AddApiClient 门面模式            复用 AddAxiosClient 注册的单例
//   4) 容器授权                         IAuthorizationProvider 单例自动装配
//   5) AddApiClient(name, ...) 多站点   命名注册 + IApiClientResolver 按站点名取用
//   6) 键名服务（.NET 8+ 官方机制）      AddKeyedSingleton + [FromKeyedServices] / GetRequiredKeyedService
// 示例自带本地 HTTP/1.1 Keep-Alive 演示服务器，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple.DependencyInjection

using var server = DemoApiServer.Start();
Console.WriteLine($"本地演示服务器: {server.Url}");

IScenario[] scenarios = typeof(IScenario).Assembly.GetTypes()
    .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IScenario).IsAssignableFrom(t))
    .Select(t => (IScenario)Activator.CreateInstance(t)!)
    .OrderBy(s => s.Number)
    .ToArray();

var items = scenarios
    .Select(s => new SampleMenuItem("DI 扩展示例", s.Number, s.GetType().Name, s.Title, () => s.RunAsync(server.Url)))
    .ToArray();

var failed = await SampleMenu.RunAsync($"AxiosNet.DependencyInjection 示例库（反射发现，共 {items.Length} 项）", items);
if (failed)
{
    Environment.ExitCode = 1;
}
