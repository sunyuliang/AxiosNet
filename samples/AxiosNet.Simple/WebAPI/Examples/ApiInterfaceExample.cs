using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 11. 接口级声明：路由写在方法上，[Path] / [Query] / [Header] / [Body] 写在参数上，
/// <c>Create&lt;T&gt;()</c> 直接得到可用代理 —— 绑定语义与请求模型完全一致，无需每个接口写请求模型。
/// </summary>
public sealed class ApiInterfaceExample : IExample
{
    /// <summary>接口代理客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 11;

    public string Title => "接口级声明：[Headers] 静态头 + 方法路由 + 参数绑定（Create<T> 代理）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl);

        // 一个接口 = 一组端点；代理按接口方法声明解析路由、绑定参数、推断默认 Body
        var api = client.Create<IUsersApi>();

        // 1) Path + 两个可选绑定（Query / Header）；接口上的 [Headers] 每个请求都带
        var one = await api.GetAsync(1, verbose: "1", trace: "iface-trace");
        Console.WriteLine($"  GET  /api/users/1 -> {one.Status} {ExampleConsole.Pretty(one.Data)}");
        Console.WriteLine($"    实际头 X-Client = {Header(one.Request, "X-Client")}，X-Trace = {Header(one.Request, "X-Trace")}");

        // 2) 未标注的数组参数按 Query 绑定，沿用 a[]=v 规则
        var page = await api.ListAsync([1, 2], role: "admin");
        Console.WriteLine($"  GET  /api/users?ids[]=1&ids[]=2&role=admin -> {page.Status} {ExampleConsole.Pretty(page.Data)}");

        // 3) POST 的单个未标注非标量参数默认作为 JSON 请求体（无需 [Body]）
        var created = await api.CreateAsync(new UserPayload("carol", 25), trace: "iface-create");
        Console.WriteLine($"  POST /api/users -> {created.Status} {ExampleConsole.Pretty(created.Data)}");
        Console.WriteLine($"    默认 Body 推断生效，服务器回显 X-Trace = {created.Data!.Trace}");
    }

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
    Task<AxiosResponse<UserProfile[]>> ListAsync(
        int[]? ids = null,
        string? role = null,
        CancellationToken cancellationToken = default);

    [Post("api/users")]
    Task<AxiosResponse<CreatedUser>> CreateAsync(
        UserPayload payload,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);
}
