using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 15. 方法级编码声明：<c>[BodyContentType]</c> 决定请求体媒体类型（表单编码由它驱动，写头不会改变编码），
/// <c>[QueryCollectionFormat]</c> 决定查询串里的集合格式（OpenAPI 的 <c>explode: true</c> 即 <see cref="AxiosCollectionFormat.Multi"/>）。
/// </summary>
public sealed class ApiInterfaceEncodingExample : IExample
{
    /// <summary>接口代理客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 15;

    public string Title => "方法级编码：[BodyContentType] 表单正文 + [QueryCollectionFormat] 重复键查询";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl);
        var api = client.Create<IEncodingApi>();

        // 1) [BodyContentType]：媒体类型决定正文编码——对象按 x-www-form-urlencoded 展平，而非 JSON。
        var login = await api.LoginFormAsync(new LoginPayload("alice", "wonderland"));
        Console.WriteLine($"  POST /api/auth/login/form -> {login.Status} {ExampleConsole.Pretty(login.Data)}");
        Console.WriteLine($"    Content-Type = {Header(login.Request, "Content-Type")}");

        // 2) [QueryCollectionFormat(Multi)]：集合按重复键发送 ids=1&ids=2。
        var multi = await api.ListByIdsAsync([1, 2]);
        Console.WriteLine($"  GET  /api/users?ids=1&ids=2 -> {multi.Status} {ExampleConsole.Pretty(multi.Data)}");

        // 3) 未声明时保持库默认的 Brackets：ids[]=1&ids[]=2。
        var brackets = await api.ListByIdsDefaultAsync([1, 2]);
        Console.WriteLine($"  GET  /api/users?ids[]=1&ids[]=2 -> {brackets.Status} {ExampleConsole.Pretty(brackets.Data)}");
    }

    /// <summary>取请求头；Content-Type 等正文头挂在 <c>request.Content.Headers</c> 上，一并查。</summary>
    private static string Header(System.Net.Http.HttpRequestMessage? request, string name)
    {
        if (request is null)
        {
            return "none";
        }

        if (request.Headers.TryGetValues(name, out var values)
            || request.Content?.Headers.TryGetValues(name, out values) == true)
        {
            return string.Join(",", values!);
        }

        return "none";
    }
}

/// <summary>编码声明样例：一个方法走表单正文，另两个方法对比 Multi 与默认 Brackets。</summary>
public interface IEncodingApi
{
    [Post("api/auth/login/form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<TokenResult>> LoginFormAsync(
        [Body] LoginPayload credentials,
        CancellationToken cancellationToken = default);

    [Get("api/users")]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
    Task<AxiosResponse<UserProfile[]>> ListByIdsAsync(
        [Query] int[] ids,
        CancellationToken cancellationToken = default);

    [Get("api/users")]
    Task<AxiosResponse<UserProfile[]>> ListByIdsDefaultAsync(
        [Query] int[] ids,
        CancellationToken cancellationToken = default);
}
