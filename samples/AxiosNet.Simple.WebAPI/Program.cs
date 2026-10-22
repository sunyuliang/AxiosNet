using AxiosNet.Simple.WebAPI;
using AxiosNet.Simple.WebAPI.Examples;

// AxiosNet.Simple.WebAPI —— AxiosNet.WebAPI 声明式 API 客户端示例
// 每个示例对应 Examples 目录下的一个类（实现 IExample），此处统一注册并顺序执行。
// 示例自带本地 HTTP/1.1 Keep-Alive 服务器，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple.WebAPI
//
// 请求模型与返回模型集中定义在 ApiModels.cs：路由在类上、参数绑定在属性上，
// IApiRequest<TResponse> 的泛型即返回类型——这正是本包的用法：模型写好，调用只剩一句 RequestAsync。

using var server = DemoApiServer.Start();

PrintBanner(server.Port);

var context = new ExampleContext(server.Url, server);

IExample[] examples =
{
    new GetUserExample(),
    new ListUsersExample(),
    new CreateUserExample(),
    new AuthorizationExample(),
    new DeleteAndErrorExample(),
    new LoggingExample(),
    new SigningExample()
};

try
{
    foreach (var example in examples)
    {
        await example.RunAsync(context);
    }

    Console.WriteLine();
    Console.WriteLine("== 全部示例运行完成 ==");
}
catch (Exception ex)
{
    Console.WriteLine($"[示例执行失败] {ex}");
    Environment.ExitCode = 1;
}

// ------------------------------------------------------------------
// 辅助
// ------------------------------------------------------------------
static void PrintBanner(int port)
{
    Console.WriteLine("AxiosNet.WebAPI 声明式 API 客户端示例");
    Console.WriteLine($"本地演示服务器: http://127.0.0.1:{port}/ (HTTP/1.1 Keep-Alive)");
}
