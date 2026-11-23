using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 11. 接口级声明：路由写在方法上，[Path] / [Query] / [Header] / [RootBody] 写在参数上，
/// <c>Create&lt;T&gt;()</c> 直接得到可用代理。
/// </summary>
public sealed class ApiInterfaceExample : IExample
{
    /// <summary>接口代理客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 11;

    public string Title => "接口级声明：[Headers] 静态头 + 方法路由 + 参数绑定（Create<T> 代理）";

    /// <summary>示例入口：接口级声明：[Headers] 静态头 + 方法路由 + 参数绑定（Create<T> 代理）。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl);

        // 一个接口 = 一组端点；代理按接口方法声明解析路由、绑定参数，并按 HTTP 方法决定默认通道（聚合 Body / Query）
        var api = client.Create<IUsersApi>();

        // 1) Path + 两个可选绑定（Query / Header）；接口上的 [Headers] 每个请求都带
        var one = await api.GetAsync(1, verbose: "1", trace: "iface-trace");
        Console.WriteLine($"  GET  /api/users/1 -> {one.Status} {ExampleConsole.Pretty(one.Data)}");
        Console.WriteLine($"    实际头 X-Client = {Header(one.Request, "X-Client")}，X-Trace = {Header(one.Request, "X-Trace")}");

        // 2) 未标注的数组参数按 Query 绑定，沿用 a[]=v 规则；方法声明返回 Task<T> 时只交付正文
        var page = await api.ListAsync([1, 2], role: "admin");
        Console.WriteLine($"  GET  /api/users?ids[]=1&ids[]=2&role=admin -> {page.Length} 人 {ExampleConsole.Pretty(page)}");

        // 3) [RootBody] 把参数直接当整个正文：{ "name": "carol", "age": 25 }（不加壳）
        var created = await api.CreateAsync(new UserPayload("carol", 25), trace: "iface-create");
        Console.WriteLine($"  POST /api/users -> {created.Status} {ExampleConsole.Pretty(created.Data)}");
        Console.WriteLine($"    服务器回显 X-Trace = {created.Data!.Trace}");
    }

    /// <summary>读请求消息上的某个头；没有请求消息时返回 <c>none</c>。</summary>
    private static string Header(System.Net.Http.HttpRequestMessage? request, string name)
        => request is not null && request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values!)
            : "none";
}

/// <summary>接口级声明样例：路由在方法上，绑定特性在参数上。</summary>
[Headers("X-Client: AxiosNet-Interface")]
public interface IUsersApi
{
    [Get("api/users/{id}")]
    Task<AxiosResponse<UserProfile>> GetAsync(
        [Path] int id,
        [Query("verbose")] string? verbose = null,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);

    [Get("api/users")]
    Task<UserProfile[]> ListAsync(
        int[]? ids = null,
        string? role = null,
        CancellationToken cancellationToken = default);

    [Post("api/users")]
    Task<AxiosResponse<CreatedUser>> CreateAsync(
        [RootBody] UserPayload payload,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);
}
