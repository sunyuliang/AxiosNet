using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 22. 请求签名：对「令牌 + 时间戳 + nonce + 方法 + 路径 + 请求体」计算 HMAC-SHA256，
/// 服务端验签以识别篡改与重放。常见于开放平台 / 网关侧的接口鉴权。
/// </summary>
public sealed class RequestSigningExample : IExample
{
    /// <summary>本示例专用客户端：签名拦截器只由本示例挂载，跨运行复用前先清空。</summary>
    private static AxiosClient? _http;

    public int Number => 22;

    public string Title => "请求签名：HMAC-SHA256(token + 请求内容)，防篡改、防重放";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建；签名拦截器每次运行重挂（令牌/密钥是每轮登录取回的），先清掉上一轮的
        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });
        http.Interceptors.Request.Clear();
        http.Interceptors.Response.Clear();

        // 登录同时取回 appSecret。真实系统里密钥按 app 预共享、不会随登录下发，这里只是为了演示闭环。
        var login = await http.PostAsync<JsonElement>(
            DemoAuthApi.LoginPath, new { username = "alice", password = "wonderland" });
        var token = login.Data.GetProperty("accessToken").GetString()!;
        var secret = login.Data.GetProperty("appSecret").GetString()!;
        Console.WriteLine($"  登录成功，拿到 accessToken={token[..8]}… 与 appSecret={secret[..8]}…");

        // 签名拦截器：每个请求自动附加 Authorization / X-Timestamp / X-Nonce / X-Signature
        http.Interceptors.Request.Use(config =>
        {
            // 已预置签名头的请求跳过（下面几项「异常场景」需要手工构造签名）
            if (config.Headers.Has("X-Signature"))
            {
                return config;
            }

            // 签名必须覆盖「最终会上送的字节」：string 类型的 Data 会被 AxiosNet 原样透传，
            // 因此先自己序列化成 JSON 字符串，再对这段文本计算哈希——与服务端收到的字节逐字节一致。
            var body = config.Data as string ?? string.Empty;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var signature = RequestSigner.Sign(
                secret, token, config.Method.Method, new Uri(http.GetUri(config)).PathAndQuery, body, timestamp, nonce);

            config.Headers.Set("Authorization", "Bearer " + token);
            config.Headers.Set("X-Timestamp", timestamp);
            config.Headers.Set("X-Nonce", nonce);
            config.Headers.Set("X-Signature", signature);
            Console.WriteLine($"      [签名] {config.Method.Method} {config.Url} bodyHash={RequestSigner.BodyHash(body)[..12]}…");
            return config;
        });

        var order = """{"sku":"AXN-1001","quantity":2,"remark":"签名演示"}""";

        // ---- 1) 正常签名请求 ----
        Console.WriteLine("  [1] 正常签名请求（POST 带 JSON 请求体）");
        var created = await http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order);
        Console.WriteLine($"      {created.Status} -> {created.Data.GetRawText()}");

        // ---- 2) 无请求体的签名请求 ----
        Console.WriteLine("  [2] 签名 GET（无请求体，bodyHash 取空串哈希）");
        var profile = await http.GetAsync<JsonElement>(DemoAuthApi.SignedProfilePath);
        Console.WriteLine($"      {profile.Status} -> {profile.Data.GetRawText()}");

        // ---- 3) 请求体被篡改：签名针对原请求体计算，实际上送改过的内容 ----
        Console.WriteLine("  [3] 请求体被篡改（签名算的是原内容，实际上送被改过的内容）");
        var tampered = """{"sku":"AXN-1001","quantity":2,"remark":"被改过的备注"}""";
        var tamperedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var tamperedNonce = Guid.NewGuid().ToString("N");
        var tamperedSignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, order, tamperedTimestamp, tamperedNonce);
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, tampered, c =>
        {
            c.Headers.Set("Authorization", "Bearer " + token);
            c.Headers.Set("X-Timestamp", tamperedTimestamp);
            c.Headers.Set("X-Nonce", tamperedNonce);
            c.Headers.Set("X-Signature", tamperedSignature);
        }));

        // ---- 4) 重放攻击：同一 nonce + 时间戳 + 签名原样再发一次 ----
        Console.WriteLine("  [4] 重放攻击（首次成功，原样重发被 nonce 缓存拒绝）");
        var replayTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var replayNonce = Guid.NewGuid().ToString("N");
        var replaySignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, order, replayTimestamp, replayNonce);
        Action<AxiosConfig> replayHeaders = c =>
        {
            c.Headers.Set("Authorization", "Bearer " + token);
            c.Headers.Set("X-Timestamp", replayTimestamp);
            c.Headers.Set("X-Nonce", replayNonce);
            c.Headers.Set("X-Signature", replaySignature);
        };

        var first = await http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, replayHeaders);
        Console.WriteLine($"      首次发送 -> {first.Status}");
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, replayHeaders));

        // ---- 5) 过期时间戳：签名本身正确，但时间戳已滑出允许窗口 ----
        Console.WriteLine("  [5] 过期时间戳（签名正确，但时间戳在 5 分钟窗口之外）");
        var staleTimestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600).ToString();
        var staleNonce = Guid.NewGuid().ToString("N");
        var staleSignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, order, staleTimestamp, staleNonce);
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, c =>
        {
            c.Headers.Set("Authorization", "Bearer " + token);
            c.Headers.Set("X-Timestamp", staleTimestamp);
            c.Headers.Set("X-Nonce", staleNonce);
            c.Headers.Set("X-Signature", staleSignature);
        }));

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 规范串 = token \\n timestamp \\n nonce \\n METHOD \\n path?query \\n sha256hex(body)，任何一项被改动服务端都能发现");
        Console.WriteLine("    · 请求体先序列化成字符串再签名（string 数据 AxiosNet 原样透传），保证签的就是上送的字节");
        Console.WriteLine("    · 防重放两道闸：时间戳窗口（容忍时钟偏差）+ nonce 一次性缓存");
        Console.WriteLine("    · 服务端用 CryptographicOperations.FixedTimeEquals 做定长时间比较，且签名同时绑定令牌——换令牌签名即失效");
    }

    private static async Task ShowSignedErrorAsync(Task<AxiosResponse<JsonElement>> request)
    {
        try
        {
            var response = await request;
            Console.WriteLine($"      HTTP {response.Status}（未抛异常）");
        }
        catch (AxiosException ex)
        {
            var message = ex.Response?.Data is JsonElement element && element.TryGetProperty("error", out var reason)
                ? reason.GetString()
                : ex.Message;
            Console.WriteLine($"      抛错 {ex.Code} / HTTP {ex.Status} / {message}");
        }
    }
}

/// <summary>HMAC-SHA256 请求签名器（客户端侧）。</summary>
internal static class RequestSigner
{
    /// <summary>请求体的 SHA256 十六进制摘要（小写）。</summary>
    public static string BodyHash(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>
    /// 计算签名。规范串：<c>token \n timestamp \n nonce \n METHOD \n path?query \n sha256hex(body)</c>。
    /// </summary>
    public static string Sign(
        string secretHex,
        string token,
        string method,
        string pathAndQuery,
        string body,
        string timestamp,
        string nonce)
    {
        var canonical = string.Join('\n',
            token, timestamp, nonce, method.ToUpperInvariant(), pathAndQuery, BodyHash(body));

        using var hmac = new HMACSHA256(Convert.FromHexString(secretHex));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
