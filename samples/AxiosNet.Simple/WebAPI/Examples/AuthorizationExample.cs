using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 4. 授权：自定义 IAuthorizationProvider 按请求类型跳过登录、其余请求自动补 Bearer；
/// 固定令牌场景用内置 BearerAuthorizationProvider 一行搞定。
/// </summary>
public sealed class AuthorizationExample : IExample
{
    public int Number => 4;

    public string Title => "授权：自定义提供方按请求类型跳过（登录不带凭据）+ 内置 Bearer";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 登录接口不需要凭据：提供方看到 LoginRequest 就放行；
        // 登录成功后把令牌交给提供方，后续请求自动加 Authorization: Bearer xxx
        var auth = new SessionAuthorizationProvider();

        using var client = new ApiClient(context.BaseUrl)
        {
            Authorization = auth
        };

        var login = await client.RequestAsync(new LoginRequest
        {
            Credentials = new LoginPayload("alice", "wonderland")
        });

        Console.WriteLine($"  登录（跳过授权）-> token = {login.Data!.Token}，有效期 {login.Data.ExpiresIn}s");
        auth.SetToken(login.Data.Token);

        var profile = await client.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  受保护端点（自动 Bearer）-> {ExampleConsole.Pretty(profile.Data)}");
        Console.WriteLine($"  实际请求头 = {profile.Request!.Headers.Authorization} / X-Device = {GetHeader(profile.Request, "X-Device")}");

        // 固定令牌的最短写法：不需要自建类
        using var staticClient = new ApiClient(context.BaseUrl)
        {
            Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken)
        };

        var same = await staticClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  固定令牌写法 -> {same.Status} {same.Data!.User}");
    }

    private static string GetHeader(System.Net.Http.HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values!) : "none";

    /// <summary>
    /// 演示用的会话授权：登录请求原样放行，其余请求补 Bearer 头。
    /// 生产实现（令牌缓存 / 过期刷新 / 单飞刷新）同样只需实现 IAuthorizationProvider；
    /// 注意授权发生在发送前，拿不到响应，令牌由调用方在登录后注入。
    /// </summary>
    private sealed class SessionAuthorizationProvider : IAuthorizationProvider
    {
        private volatile string? _token;

        public void SetToken(string token) => _token = token;

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            if (context.Request is LoginRequest)
            {
                return ValueTask.CompletedTask;
            }

            context.Config.Headers.Set("Authorization", $"Bearer {_token}");
            return ValueTask.CompletedTask;
        }
    }
}
