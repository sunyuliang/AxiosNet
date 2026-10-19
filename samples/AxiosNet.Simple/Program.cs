using AxiosNet.Simple;
using AxiosNet.Simple.Examples;

// AxiosNet.Simple —— AxiosNet 各类集成调用示例
// 每个示例对应 Examples 目录下的一个类（实现 IExample），此处统一注册并顺序执行。
// 示例自带本地 HTTP/1.1 Keep-Alive 服务器，离线即可运行：
//   dotnet run --project samples/AxiosNet.Simple

using var server = DemoServer.Start();

PrintBanner(server.Port);

var context = new ExampleContext(server.Url, server);

IExample[] examples =
{
    new StaticFacadeExample(),
    new CreateInstanceExample(),
    new PostJsonExample(),
    new PostFormExample(),
    new ParamsSerializationExample(),
    new BasicAuthExample(),
    new DynamicResponseExample(),
    new DynamicRequestExample(),
    new ResponseTypesExample(),
    new InterceptorsExample(),
    new ErrorHandlingExample(),
    new ConnectionPoolExample(),
    new MultipartExample(),
    new DownloadExample(),
    new GetUriAndLimitsExample(),
    new ProgressAndRateExample()
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
    Console.WriteLine("AxiosNet 集成调用示例");
    Console.WriteLine($"本地演示服务器: http://127.0.0.1:{port}/ (HTTP/1.1 Keep-Alive)");
}
