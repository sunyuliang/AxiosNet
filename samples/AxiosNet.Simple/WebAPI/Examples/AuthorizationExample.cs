using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 4. 授权：<c>[AllowAnonymous]</c> / <c>[Authorize]</c> 声明 + <see cref="IAccessTokenProvider"/> 取令牌
/// （拼头交给库）、<see cref="AccessTokenManager"/> 的缓存与刷新，以及与完全自定义的 <see cref="IAuthorizationProvider"/> 叠加对比。
/// </summary>
public sealed class AuthorizationExample : IExample
{
    /// <summary>令牌提供方客户端：客户端静态复用，提供方按运行重新挂载。</summary>
    private static ApiClient? _tokenClient;

    /// <summary>叠加自定义授权客户端：客户端静态复用，两个提供方按运行重新挂载。</summary>
    private static ApiClient? _layered;

    /// <summary>固定令牌客户端：令牌固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _fixedToken;

    public int Number => 4;

    public string Title => "授权：[AllowAnonymous] / [Authorize] + 令牌提供方 / AccessTokenManager（对比自定义 IAuthorizationProvider）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 令牌提供方只负责「取令牌」，Authorization: Bearer xxx 由库按 scheme 拼
        var tokens = new SessionTokenProvider();

        // 三个客户端都静态复用（首次运行创建）；授权提供方每轮新建，运行前重新挂上
        var client = _tokenClient ??= new ApiClient(context.BaseUrl);
        client.AccessTokenProvider = tokens;

        // 1) 登录模型标了 [AllowAnonymous]：整条授权链路跳过（提供方不会被调用），也不带 Authorization 头
        var login = await client.RequestAsync(new LoginRequest
        {
            Credentials = new LoginPayload("alice", "wonderland")
        });

        Console.WriteLine($"  登录（[AllowAnonymous]）-> token = {login.Data!.Token}");
        Console.WriteLine($"    实际头 Authorization = {Header(login.Request, "Authorization")}，"
            + $"令牌提供方被调用 {tokens.Calls} 次");

        tokens.SetToken(login.Data.Token);

        // 2) [Authorize]：库自动拼 Authorization: Bearer {token}
        var profile = await client.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  受保护端点（[Authorize] 自动 Bearer）-> {ExampleConsole.Pretty(profile.Data)}");
        Console.WriteLine($"    实际头 Authorization = {Header(profile.Request, "Authorization")}");

        // 3) 令牌为空时不写头：服务端 401 照常走统一错误模型（[Authorize] 不会伪造凭据）
        tokens.SetToken(null);
        try
        {
            await client.RequestAsync(new SecureProfileRequest());
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  令牌为空 -> {ex.Code} / HTTP {ex.Status}（未写 Authorization 头）");
        }

        // 4) 叠加自定义 IAuthorizationProvider：它最后执行，能看到令牌提供方写的头，也可以覆盖
        tokens.SetToken(DemoApiServer.DemoToken);
        var observer = new HeaderObserver();
        var layered = _layered ??= new ApiClient(context.BaseUrl);
        layered.AccessTokenProvider = tokens;
        layered.Authorization = observer;

        await layered.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  IAuthorizationProvider 最后执行，看到的 Authorization = {observer.Seen}");

        // 5) 固定令牌的最短写法：不需要任何提供方（令牌固定，创建一次即可）
        var fixedClient = _fixedToken ??= new ApiClient(context.BaseUrl)
        {
            Authorization = new BearerAuthorizationProvider(DemoApiServer.DemoToken)
        };

        var same = await fixedClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  固定令牌写法 -> {same.Status} {same.Data!.User}");

        // 6) 令牌管理器：缓存 + 到期前刷新 + 并发刷新单飞；取令牌的 HTTP 调用仍由调用方提供
        var acquires = 0;
        using var managed = new AccessTokenManager(new AccessTokenOptions(_ =>
        {
            Interlocked.Increment(ref acquires);
            return ValueTask.FromResult(new AccessToken(DemoApiServer.DemoToken, DateTimeOffset.UtcNow.AddMinutes(5)));
        }));

        using var managedClient = new ApiClient(context.BaseUrl) { AccessTokenProvider = managed };
        await managedClient.RequestAsync(new SecureProfileRequest());
        await managedClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  令牌管理器：两次请求只取令牌 {acquires} 次（未进刷新窗口时直接走缓存）");

        // 7) 401 重放：服务端把第一个令牌判为过期，客户端刷新令牌后自动重放一次
        var rotateCalls = 0;
        using var rotating = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref rotateCalls)))));
        using var rotatingClient = new ApiClient(context.BaseUrl) { AccessTokenProvider = rotating };

        var rotated = await rotatingClient.RequestAsync(new RotatingProfileRequest());
        Console.WriteLine($"  401 重放 -> {rotated.Status} {rotated.Data!.IssuedBy}（取令牌 {rotateCalls} 次，只重放一次）");
    }

    private static string Header(System.Net.Http.HttpRequestMessage? request, string name)
        => request is not null && request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values!)
            : "none";

    /// <summary>
    /// 演示用的会话令牌：登录前返回 null（不写头），登录后返回服务端下发的令牌。
    /// 令牌的缓存与刷新由实现方负责，库不缓存令牌。
    /// </summary>
    private sealed class SessionTokenProvider : IAccessTokenProvider
    {
        private volatile string? _token;
        private int _calls;

        /// <summary>被调用次数（用于证明 [AllowAnonymous] 真的跳过了授权链路）。</summary>
        public int Calls => Volatile.Read(ref _calls);

        public void SetToken(string? token) => _token = token;

        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return new ValueTask<string?>(_token);
        }
    }

    /// <summary>只观察不修改：证明 <see cref="IAuthorizationProvider"/> 在令牌提供方之后执行。</summary>
    private sealed class HeaderObserver : IAuthorizationProvider
    {
        public string? Seen { get; private set; }

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Seen = context.Config.Headers.Get("Authorization");
            return ValueTask.CompletedTask;
        }
    }
}
