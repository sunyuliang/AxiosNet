using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 21. API 令牌（Bearer Token）验证：登录签发、请求拦截器自动携带、服务端验证，
/// 以及 401 后「刷新令牌 → 重试一次」的完整闭环。
/// </summary>
public sealed class BearerTokenExample : IExample
{
    public int Number => 21;

    public string Title => "Bearer Token：登录签发、自动携带、服务端验证、401 刷新重试";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });
        var session = new TokenSession(http);

        InstallInterceptors(http, session);

        // ---- 1) 未携带令牌访问受保护资源：服务端 401 ----
        Console.WriteLine("  [1] 未登录直接访问 /api/orders");
        await ShowErrorAsync(http.GetAsync("/api/orders"));

        // ---- 2) 密码错误：服务端校验凭据，401 ----
        Console.WriteLine("  [2] 错误密码登录（凭据校验失败）");
        await ShowErrorAsync(http.PostAsync(
            DemoAuthApi.LoginPath, new { username = "alice", password = "wrong-password" }));

        // ---- 3) 正确登录：签发 access / refresh 令牌 ----
        Console.WriteLine("  [3] 正确登录，签发令牌");
        await session.LoginAsync();

        // ---- 4) 请求拦截器自动附加 Authorization: Bearer … ----
        Console.WriteLine("  [4] 自动携带令牌访问受保护资源与验签端点");
        var orders = await http.GetAsync<JsonElement>("/api/orders");
        Console.WriteLine($"      GET /api/orders -> {orders.Status} user={orders.Data.GetProperty("user").GetString()}");
        Console.WriteLine($"         orders = {orders.Data.GetProperty("orders").GetRawText()}");

        var verify = await http.GetAsync<JsonElement>("/api/token/verify");
        Console.WriteLine($"      GET /api/token/verify -> {verify.Status} {verify.Data.GetRawText()}");

        // ---- 5) 令牌过期：响应拦截器捕获 401 → 刷新 → 自动重试 ----
        Console.WriteLine("  [5] 令牌过期（重新登录换一个 1 秒过期的短令牌），再访问受保护资源");
        await session.LoginAsync(ttlSeconds: 1);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Console.WriteLine("      令牌已过期，发起请求——拦截器会自动刷新并重试……");
        var retried = await http.GetAsync<JsonElement>("/api/orders");
        Console.WriteLine($"      重试结果 -> {retried.Status} user={retried.Data.GetProperty("user").GetString()}（新令牌取回数据）");

        // ---- 6) 登出吊销会话：刷新同样失败，401 最终抛给调用方 ----
        Console.WriteLine("  [6] 登出吊销会话后再访问：连刷新也失效，401 抛给调用方");
        await http.PostAsync("/api/token/logout");
        await ShowErrorAsync(http.GetAsync("/api/orders"));

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 令牌由请求拦截器统一附加，业务代码不接触 Authorization 头");
        Console.WriteLine("    · 响应拦截器按「401 + 非令牌端点 + 未重试过 + 持有 refresh token」四个条件才触发刷新：未登录的 401 是认证失败而非过期，直接抛给调用方");
        Console.WriteLine("    · 重发用的是 error.Config（失败请求的合并配置快照），MergeConfig 幂等，请求拦截器会把新令牌重新盖上去");
        Console.WriteLine("    · 并发 401 时刷新用 SemaphoreSlim 单闸串行化，真实项目里还应做「双重检查 + 失败让排队请求快速失败」");
    }

    /// <summary>
    /// 请求拦截器：自动附加 AccessToken（登录 / 刷新端点除外——它们一个不带令牌、一个用 body 里的 refreshToken）。
    /// 响应拦截器：401 → 刷新令牌 → 带 X-Token-Retried 标记重试一次。
    /// </summary>
    private static void InstallInterceptors(AxiosClient http, TokenSession session)
    {
        http.Interceptors.Request.Use(config =>
        {
            var token = session.AccessToken;
            if (token is not null
                && config.Url?.Contains("/api/token/login", StringComparison.OrdinalIgnoreCase) != true
                && config.Url?.Contains("/api/token/refresh", StringComparison.OrdinalIgnoreCase) != true)
            {
                config.Headers.Set("Authorization", "Bearer " + token);
            }

            return config;
        });

        http.Interceptors.Response.Use(
            onFulfilled: async response => response,
            onRejected: async ex =>
            {
                if (ex is not AxiosException error || error.Response?.Status != 401)
                {
                    throw ex;
                }

                // 属性不参与可空流分析，先落到局部变量再判断
                var config = error.Config;
                if (config is null
                    || config.Url?.Contains("/api/token/login", StringComparison.OrdinalIgnoreCase) == true
                    || config.Url?.Contains("/api/token/refresh", StringComparison.OrdinalIgnoreCase) == true
                    || config.Headers.Has("X-Token-Retried")
                    || session.RefreshToken is null)
                {
                    // 登录 / 刷新端点自身的 401、已重试过、或从未登录（没有刷新令牌）：
                    // 这不是「过期」，是认证失败，直接抛给调用方
                    throw ex;
                }

                Console.WriteLine($"      [401] 服务端拒绝当前令牌（{Describe(error)}），自动刷新并重试");
                await session.RefreshAsync();

                config.Headers.Set("X-Token-Retried", "1");
                return await http.RequestAsync(config);
            });
    }

    private static string Describe(AxiosException error)
        => error.Response?.Data is JsonElement element && element.TryGetProperty("error", out var reason)
            ? reason.GetString() ?? error.Message
            : error.Message;

    private static async Task ShowErrorAsync(Task<AxiosResponse> request)
    {
        try
        {
            var response = await request;
            Console.WriteLine($"      HTTP {response.Status}（未抛异常）");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"      抛错 {ex.Code} / HTTP {ex.Status} / {Describe(ex)}");
        }
    }

    /// <summary>
    /// 客户端令牌会话：保存 access / refresh 令牌；刷新用单闸串行化，避免并发 401 时多次刷新互相轮换作废。
    /// </summary>
    private sealed class TokenSession
    {
        private readonly AxiosClient _http;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public string? AccessToken { get; private set; }

        public string? RefreshToken { get; private set; }

        public TokenSession(AxiosClient http) => _http = http;

        public async Task LoginAsync(int ttlSeconds = 300)
        {
            var response = await _http.PostAsync<JsonElement>(
                $"{DemoAuthApi.LoginPath}?ttl={ttlSeconds}",
                new { username = "alice", password = "wonderland" });

            Apply(response.Data);
            Console.WriteLine(
                $"      签发成功 access={Shorten(AccessToken)} refresh={Shorten(RefreshToken)} "
                + $"expiresIn={response.Data.GetProperty("expiresIn").GetInt32()}s");
        }

        public async Task RefreshAsync()
        {
            await _gate.WaitAsync();
            try
            {
                var refreshToken = RefreshToken;
                if (refreshToken is null)
                {
                    throw new InvalidOperationException("会话中没有 refresh token，无法刷新");
                }

                Console.WriteLine("      [刷新] 用 refresh token 轮换新的 access token …");
                var response = await _http.PostAsync<JsonElement>(
                    DemoAuthApi.RefreshPath, new { refreshToken });

                Apply(response.Data);
                Console.WriteLine($"      [刷新] 完成，新 access={Shorten(AccessToken)}");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void Apply(JsonElement data)
        {
            AccessToken = data.GetProperty("accessToken").GetString();
            RefreshToken = data.GetProperty("refreshToken").GetString();
        }

        private static string Shorten(string? token) => token is null ? "<null>" : token[..8] + "…";
    }
}
