using System.Text;
using AxiosNet.WebAPI;
using Generated = AxiosNet.Simple.WebAPI.Generated;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 16. OpenAPI 导入：<c>axiosnet-gen</c> 读取 <c>WebAPI/demo.openapi.json</c>，默认生成每个操作一个
/// <c>IApiRequest&lt;T&gt;</c> 请求模型（tag 分组 / 根级授权 / 表单登录 / multipart / SSE 与 JSON 数组流式），
/// 调用跑的是 DemoApiServer 的真实路由；加 <c>--style interface</c> 则改为 <c>Create&lt;T&gt;()</c> 接口代理。
/// 重新生成（仓库根目录执行）：<c>dotnet run --project src/AxiosNet.Cli -f net8.0 -- samples/AxiosNet.Simple/WebAPI/demo.openapi.json -o samples/AxiosNet.Simple/WebAPI/Generated -n AxiosNet.Simple.WebAPI.Generated --force</c>。
/// </summary>
public sealed class OpenApiGeneratedExample : IExample
{
    /// <summary>生成代码的客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 16;

    public string Title => "OpenAPI 导入：axiosnet-gen 生成的请求模型（tag 分组 / 授权 / multipart / 流式）";

    /// <summary>示例入口：OpenAPI 导入：axiosnet-gen 生成的请求模型（tag 分组 / 授权 / multipart / 流式）。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 生成的请求模型由源生成器在编译期烘焙声明（Native AOT 同样可用），这里只负责发请求。
        var client = _client ??= new ApiClient(context.BaseUrl)
        {
            Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken)
        };

        // 1) tag=users → UsersRequests.g.cs：查询数组按 explode=true 生成，类上 [Authorize] 自动带 Bearer 头
        var listed = await client.RequestAsync(new Generated.ListUsersRequest { Role = "admin", Ids = [1] });
        Console.WriteLine($"  GET    /api/users?role=admin&ids=1 -> {listed.Status} {ExampleConsole.Pretty(listed.Data)}");

        var created = await client.RequestAsync(new Generated.CreateUserRequest
        {
            Body = new Generated.NewUser { Name = "carol", Age = 22 }
        });
        Console.WriteLine($"  POST   /api/users -> {created.Status} {ExampleConsole.Pretty(created.Data)}");

        var deleted = await client.RequestAsync(new Generated.DeleteUserRequest { Id = 2 });
        Console.WriteLine($"  DELETE /api/users/2 -> {deleted.Status} {ExampleConsole.Pretty(deleted.Data)}");

        // 2) tag=auth → AuthRequests.g.cs：两个登录请求来自 security: []，类上生成 [AllowAnonymous]
        var login = await client.RequestAsync(new Generated.LoginRequest
        {
            Body = new Generated.LoginPayload { Username = "alice", Password = "wonderland" }
        });
        Console.WriteLine($"  POST   /api/auth/login（JSON）-> {login.Status} token={login.Data!.Token}");

        // 表单正文没有方法级特性可写，生成器把编码方式写进 Configure（config.ContentType）。
        var form = await client.RequestAsync(new Generated.LoginFormRequest
        {
            Body = new Generated.LoginPayload { Username = "alice", Password = "wonderland" }
        });
        Console.WriteLine($"  POST   /api/auth/login/form（Configure 决定编码）-> {form.Status} user={form.Data!.User}");

        // 3) 继承根级 security 的操作在自己的请求模型上生成 [Authorize]
        var profile = await client.RequestAsync(new Generated.GetProfileRequest { XDevice = "sample-console" });
        Console.WriteLine($"  GET    /api/secure/profile（[Authorize]）-> {profile.Status} {ExampleConsole.Pretty(profile.Data)}");

        // 4) multipart/form-data 被展开成请求模型上的 [Part] 属性
        var upload = await client.RequestAsync(new Generated.UploadRequest
        {
            Title = "OpenAPI 导入示例",
            File = Encoding.UTF8.GetBytes("这是由生成代码发送的文件内容。")
        });
        Console.WriteLine($"  POST   /api/upload（[Multipart]）-> {upload.Status} {ExampleConsole.Pretty(upload.Data)}");

        // 5) 流式端点：帧格式落在请求模型的 StreamFormat 常量上（[Stream] 只能标在接口方法上）
        var sse = await client.RequestStreamAsync(
            new Generated.StreamEventsRequest(),
            Generated.StreamEventsRequest.StreamFormat);
        await foreach (var frame in sse.Data!)
        {
            Console.WriteLine($"  SSE    <- 第 {frame.Index} 帧 {frame.Text}");
        }

        var pages = await client.RequestStreamAsync(
            new Generated.StreamPagesRequest(),
            Generated.StreamPagesRequest.StreamFormat);
        await foreach (var page in pages.Data!)
        {
            Console.WriteLine($"  JSON[] <- 第 {page.Index} 页 {page.Text}");
        }
    }
}
