using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 12. 接口级契约的授权与校验：<c>[Authorize]</c> / <c>[AllowAnonymous]</c> 标在接口或接口方法上，
/// 代理发请求时自动拼 Authorization；方法级 <c>[Headers]</c> 覆盖接口级同名项；
/// <see cref="ApiClient.ValidateInterfaces(Type[])"/> 在启动期把写错的接口逐条报出来。
/// </summary>
public sealed class ApiInterfaceContractExample : IExample
{
    /// <summary>带固定令牌的客户端：首次运行创建后跨运行复用。</summary>
    private static ApiClient? _client;

    public int Number => 12;

    public string Title => "接口级契约：[Authorize] / [AllowAnonymous] 授权 + 方法级 [Headers] + ValidateInterfaces 静态校验";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        var client = _client ??= new ApiClient(context.BaseUrl)
        {
            Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken)
        };

        // 1) [AllowAnonymous]：整条授权链路跳过，不写 Authorization 头（登录本身不需要凭据）
        var login = await client.Create<IAnonymousAuthApi>()
            .LoginAsync(new LoginPayload("alice", "wonderland"));
        Console.WriteLine($"  POST /api/auth/login -> {login.Status} {ExampleConsole.Pretty(login.Data)}");
        Console.WriteLine($"    实际头 Authorization = {Header(login.Request, "Authorization")}（[AllowAnonymous] 跳过授权）");

        // 2) [Authorize]：接口上的声明让每个方法都带同一个令牌
        var profile = await client.Create<IAuthorizedAuthApi>().ProfileAsync("sample-console");
        Console.WriteLine($"  GET  /api/secure/profile -> {profile.Status} {ExampleConsole.Pretty(profile.Data)}");
        Console.WriteLine($"    实际头 Authorization = {Header(profile.Request, "Authorization")}");

        // 3) 方法级 [Headers]：同名项覆盖接口级，其余接口级头照常保留
        var scoped = await client.Create<IAuthorizedAuthApi>().ScopedProfileAsync("sample-console");
        Console.WriteLine($"  GET  /api/secure/profile -> {scoped.Status} {ExampleConsole.Pretty(scoped.Data)}");
        Console.WriteLine($"    实际头 X-Scope = {Header(scoped.Request, "X-Scope")}（接口级是 interface，方法级覆盖成 method）");

        // 4) 启动期校验：写错的接口不等到发请求才炸，ValidateInterfaces 一次返回全部问题
        var errors = ApiClient.ValidateInterfaces(typeof(IMissingRouteAuthApi), typeof(IWriterApi));
        Console.WriteLine($"  ValidateInterfaces 发现 {errors.Count} 条问题：");
        foreach (var error in errors)
        {
            Console.WriteLine($"    - {error}");
        }
    }

    private static string Header(System.Net.Http.HttpRequestMessage? request, string name)
        => request is not null && request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values!)
            : "none";
}

/// <summary>匿名接口：所有方法跳过授权链路。</summary>
[AllowAnonymous]
public interface IAnonymousAuthApi
{
    [Post("api/auth/login")]
    Task<AxiosResponse<TokenResult>> LoginAsync(
        [Body] LoginPayload credentials,
        CancellationToken cancellationToken = default);
}

/// <summary>受保护接口：所有方法自动拼 <c>Authorization: Bearer {token}</c>，并共用接口级静态头。</summary>
[Authorize]
[Headers("X-Scope: interface")]
public interface IAuthorizedAuthApi
{
    [Get("api/secure/profile")]
    Task<AxiosResponse<SecureProfile>> ProfileAsync(
        [Header("X-Device")] string device,
        CancellationToken cancellationToken = default);

    /// <summary>方法级 <c>[Headers]</c>：同名项覆盖接口级，不同名项追加。</summary>
    [Get("api/secure/profile")]
    [Headers("X-Scope: method")]
    Task<AxiosResponse<SecureProfile>> ScopedProfileAsync(
        [Header("X-Device")] string device,
        CancellationToken cancellationToken = default);
}

// ---- 故意写错的声明：只用于演示 ValidateInterfaces 的报错，不会被实际调用 ----

/// <summary>缺少路由特性。</summary>
public interface IMissingRouteAuthApi
{
    Task<AxiosResponse<UserProfile>> GetAsync(int id);
}

/// <summary>返回类型不是 Task&lt;AxiosResponse&lt;T&gt;&gt;。</summary>
public interface IWriterApi
{
    [Get("api/users")]
    Task<UserProfile[]> ListAsync();
}
