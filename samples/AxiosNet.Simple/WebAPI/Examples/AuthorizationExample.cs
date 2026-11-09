using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 4. 授权：<c>[AllowAnonymous]</c> / <c>[Authorize]</c> 声明 + <see cref="IApiCredentialProvider"/> 的托管凭据与自定义凭据、
/// <see cref="AccessTokenManager"/> 的缓存与刷新、用同一个客户端的匿名登录接口自举取令牌，以及凭据落在任意请求头 / 查询参数的写法。
/// </summary>
public sealed class AuthorizationExample : IExample
{
    /// <summary>会话令牌客户端：客户端静态复用，提供方按运行重新挂载。</summary>
    private static ApiClient? _tokenClient;

    /// <summary>自定义凭据客户端：客户端静态复用，提供方按运行重新挂载。</summary>
    private static ApiClient? _customClient;

    /// <summary>固定令牌客户端：令牌固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _fixedToken;

    /// <summary>令牌管理器客户端：客户端静态复用，管理器按运行重新挂载。</summary>
    private static ApiClient? _managedClient;

    /// <summary>401 重放客户端：客户端静态复用，轮换管理器按运行重新挂载。</summary>
    private static ApiClient? _rotatingClient;

    /// <summary>query 令牌客户端：客户端静态复用，提供方按运行重新挂载。</summary>
    private static ApiClient? _queryClient;

    /// <summary>请求头令牌客户端：凭据固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _keyClient;

    public int Number => 4;

    public string Title => "授权：[AllowAnonymous] / [Authorize] + 统一凭据提供方 / AccessTokenManager / 自举取令牌 / 凭据落点（任意头 / 查询参数）";

    /// <summary>示例入口：统一凭据提供方的四种形态、令牌缓存与 401 重放，以及自举取令牌。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 托管凭据只负责「取令牌」，Authorization: Bearer xxx 由库按 scheme 拼
        var tokens = new SessionCredentialProvider();

        var client = _tokenClient ??= new ApiClient(context.BaseUrl);
        client.CredentialProvider = tokens;

        // 1) 登录模型标了 [AllowAnonymous]：整条授权链路跳过（提供方不会被调用），也不带 Authorization 头
        var login = await client.RequestAsync(new LoginRequest
        {
            Credentials = new LoginPayload("alice", "wonderland")
        });

        Console.WriteLine($"  登录（[AllowAnonymous]）-> token = {login.Data!.Token}");
        Console.WriteLine($"    实际头 Authorization = {Header(login.Request, "Authorization")}，"
            + $"凭据提供方被调用 {tokens.Calls} 次");

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

        // 4) 自定义凭据：提供方自己写任意头（签名等），返回 Custom() 后库不再碰头、也不做 401 重放
        var custom = new CustomCredentialProvider();
        var customClient = _customClient ??= new ApiClient(context.BaseUrl);
        customClient.CredentialProvider = custom;

        var signed = await customClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  自定义凭据 -> {signed.Status}，实际头 Authorization = {Header(signed.Request, "Authorization")}，"
            + $"X-Signed = {Header(signed.Request, "X-Signed")}");

        // 5) 固定凭据的最短写法：不需要自己实现提供方
        var fixedClient = _fixedToken ??= new ApiClient(context.BaseUrl)
        {
            CredentialProvider = CredentialProviders.Fixed(DemoApiServer.DemoToken)
        };

        var same = await fixedClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  固定凭据写法 -> {same.Status} {same.Data!.User}");

        // 6) 令牌管理器：缓存 + 到期前刷新 + 并发刷新单飞；取令牌的 HTTP 调用仍由调用方提供
        var acquires = 0;
        using var managed = new AccessTokenManager(new AccessTokenOptions(_ =>
        {
            Interlocked.Increment(ref acquires);
            return ValueTask.FromResult(new AccessToken(DemoApiServer.DemoToken, DateTimeOffset.UtcNow.AddMinutes(5)));
        }));

        var managedClient = _managedClient ??= new ApiClient(context.BaseUrl);
        managedClient.CredentialProvider = managed;
        await managedClient.RequestAsync(new SecureProfileRequest());
        await managedClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  令牌管理器：两次请求只取令牌 {acquires} 次（未进刷新窗口时直接走缓存）");

        // 7) 401 重放：服务端把第一个令牌判为过期，客户端刷新令牌后自动重放一次
        var rotateCalls = 0;
        using var rotating = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref rotateCalls)))));
        var rotatingClient = _rotatingClient ??= new ApiClient(context.BaseUrl);
        rotatingClient.CredentialProvider = rotating;

        var rotated = await rotatingClient.RequestAsync(new RotatingProfileRequest());
        Console.WriteLine($"  401 重放 -> {rotated.Status} {rotated.Data!.IssuedBy}（取令牌 {rotateCalls} 次，只重放一次）");

        // 8) 自举取令牌：登录接口与业务接口共用一个 ApiClient，[AllowAnonymous] 让取令牌不会重入凭据链路
        ApiClient? selfHosted = null;
        var logins = 0;
        using var selfHostedTokens = new AccessTokenManager(new AccessTokenOptions(async ct =>
        {
            var result = await selfHosted!.RequestAsync(new LoginRequest
            {
                Credentials = new LoginPayload("alice", "wonderland")
            }, cancellationToken: ct);
            Interlocked.Increment(ref logins);
            return new AccessToken(result.Data!.Token, DateTimeOffset.UtcNow.AddSeconds(result.Data.ExpiresIn));
        }));

        selfHosted = new ApiClient(context.BaseUrl) { CredentialProvider = selfHostedTokens };
        using var selfHostedClient = selfHosted;

        var bootstrapped = await selfHostedClient.RequestAsync(new SecureProfileRequest());
        await selfHostedClient.RequestAsync(new SecureProfileRequest());
        Console.WriteLine($"  自举取令牌 -> {bootstrapped.Status} {bootstrapped.Data!.User}"
            + $"（登录 {logins} 次，第二次请求走缓存）");

        // 9) 落点不止 Authorization 头：托管凭据一样能写查询参数 / 任意请求头，401 后同样刷新重放
        using var queryTokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken(DemoApiServer.DemoToken, DateTimeOffset.UtcNow.AddMinutes(5)))));
        var queryClient = _queryClient ??= new ApiClient(context.BaseUrl);
        queryClient.CredentialProvider = CredentialProviders.Create(async (_, ct) =>
            ApiCredential.Query("access_token", await queryTokens.GetTokenAsync(ct)));

        var queryProfile = await queryClient.RequestAsync(new QueryTokenProfileRequest());
        Console.WriteLine($"  query 落点 -> {queryProfile.Status} {queryProfile.Data!.User}，"
            + $"实际 query = {queryProfile.Request?.RequestUri?.Query}，"
            + $"实际头 Authorization = {Header(queryProfile.Request, "Authorization")}");

        // 10) 任意请求头落点：API Key 类接口，库写 X-Api-Key，Authorization 头不参与
        var keyClient = _keyClient ??= new ApiClient(context.BaseUrl)
        {
            CredentialProvider = CredentialProviders.Fixed(ApiCredential.Header("X-Api-Key", DemoApiServer.DemoToken))
        };

        var keyProfile = await keyClient.RequestAsync(new KeyTokenProfileRequest());
        Console.WriteLine($"  请求头落点 -> {keyProfile.Status} {keyProfile.Data!.User}，"
            + $"实际头 X-Api-Key = {Header(keyProfile.Request, "X-Api-Key")}，"
            + $"实际头 Authorization = {Header(keyProfile.Request, "Authorization")}");
    }

    /// <summary>读请求消息上的某个头；短路响应没有请求消息，返回 <c>none</c>。</summary>
    private static string Header(System.Net.Http.HttpRequestMessage? request, string name)
        => request is not null && request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values!)
            : "none";

    /// <summary>演示用的会话凭据：登录前返回空白（不写头），登录后返回服务端下发的令牌。</summary>
    private sealed class SessionCredentialProvider : IApiCredentialProvider
    {
        // 当前会话令牌；登录前为 null，此时不写 Authorization 头。
        private volatile string? _token;
        private int _calls;

        /// <summary>被调用次数（用于证明 [AllowAnonymous] 真的跳过了授权链路）。</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>换会话令牌：下一次取凭据就返回新值。</summary>
        public void SetToken(string? token) => _token = token;

        /// <summary>取当前会话凭据并计一次调用。</summary>
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(ApiCredential.Token(_token));
        }
    }

    /// <summary>自定义凭据：自己写 Authorization 与签名头，库不托管也不重放。</summary>
    private sealed class CustomCredentialProvider : IApiCredentialProvider
    {
        /// <summary>取凭据时写任意头，再返回 Custom()。</summary>
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            context.Config.Headers.Set("Authorization", "Bearer " + DemoApiServer.DemoToken);
            context.Config.Headers.Set("X-Signed", "sha256=demo");
            return ValueTask.FromResult(ApiCredential.Custom());
        }
    }
}
