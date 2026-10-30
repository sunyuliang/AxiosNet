using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>22. 请求签名：对「令牌 + 时间戳 + nonce + 方法 + 路径 + 请求体」计算 HMAC-SHA256，服务端验签防篡改与重放。</summary>
public sealed class RequestSigningExample : IExample
{
    /// <summary>本示例专用客户端：签名拦截器只由本示例挂载，跨运行复用前先清空。</summary>
    private static AxiosClient? _http;

    public int Number => 22;

    public string Title => "请求签名：HMAC-SHA256(token + 请求内容)，防篡改、防重放";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        // 登录同时取回 appSecret（示例把密钥随登录下发，便于跑通闭环）
        var login = await http.PostAsync<JsonElement>(DemoAuthApi.LoginPath, new { username = "alice", password = "wonderland" });
        var token = login.Data.GetProperty("accessToken").GetString()!;
        var secret = login.Data.GetProperty("appSecret").GetString()!;
        Console.WriteLine($"  登录成功，拿到 accessToken={token[..8]}… 与 appSecret={secret[..8]}…");

        // 发送前钩子：拿到的是最终请求（URL 已拼好、正文已序列化），签的就是实际发出的字节。
        // 每次尝试各触发一次（含重试），时间戳与 nonce 因此天然不会在重试里重复。
        void UseSigning(AxiosConfig config)
            => config.Callbacks.OnSendingRequest = (request, _, cancellationToken)
                => SignAsync(request, secret, token, cancellationToken);

        var order = """{"sku":"AXN-1001","quantity":2,"remark":"签名演示"}""";

        // ---- 1) 正常签名请求 ----
        Console.WriteLine("  [1] 正常签名请求（POST 带 JSON 请求体）");
        var created = await http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, UseSigning);
        Console.WriteLine($"      {created.Status} -> {created.Data.GetRawText()}");

        // ---- 2) 无请求体的签名请求 ----
        Console.WriteLine("  [2] 签名 GET（无请求体，bodyHash 取空串哈希）");
        var profile = await http.GetAsync<JsonElement>(DemoAuthApi.SignedProfilePath, UseSigning);
        Console.WriteLine($"      {profile.Status} -> {profile.Data.GetRawText()}");

        // ---- 3) 请求体被篡改：签名针对原请求体计算，实际上送改过的内容 ----
        Console.WriteLine("  [3] 请求体被篡改（签名算的是原内容，实际上送被改过的内容）");
        var tampered = """{"sku":"AXN-1001","quantity":2,"remark":"被改过的备注"}""";
        var tamperedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var tamperedNonce = Guid.NewGuid().ToString("N");
        var tamperedSignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, Encoding.UTF8.GetBytes(order), tamperedTimestamp, tamperedNonce);
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, tampered, c =>
        {
            UseSigning(c);
            SetSignatureHeaders(c, token, tamperedTimestamp, tamperedNonce, tamperedSignature);
        }));

        // ---- 4) 重放攻击：同一 nonce + 时间戳 + 签名原样再发一次 ----
        Console.WriteLine("  [4] 重放攻击（首次成功，原样重发被 nonce 缓存拒绝）");
        var replayTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var replayNonce = Guid.NewGuid().ToString("N");
        var replaySignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, Encoding.UTF8.GetBytes(order), replayTimestamp, replayNonce);
        Action<AxiosConfig> replayHeaders = c =>
        {
            UseSigning(c);
            SetSignatureHeaders(c, token, replayTimestamp, replayNonce, replaySignature);
        };

        var first = await http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, replayHeaders);
        Console.WriteLine($"      首次发送 -> {first.Status}");
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, replayHeaders));

        // ---- 5) 过期时间戳：签名本身正确，但时间戳已滑出允许窗口 ----
        Console.WriteLine("  [5] 过期时间戳（签名正确，但时间戳在 5 分钟窗口之外）");
        var staleTimestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600).ToString();
        var staleNonce = Guid.NewGuid().ToString("N");
        var staleSignature = RequestSigner.Sign(
            secret, token, "POST", DemoAuthApi.SignedOrdersPath, Encoding.UTF8.GetBytes(order), staleTimestamp, staleNonce);
        await ShowSignedErrorAsync(http.PostAsync<JsonElement>(DemoAuthApi.SignedOrdersPath, order, c =>
        {
            UseSigning(c);
            SetSignatureHeaders(c, token, staleTimestamp, staleNonce, staleSignature);
        }));

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 规范串 = token \\n timestamp \\n nonce \\n METHOD \\n path?query \\n sha256hex(body)，任何一项被改动服务端都能发现");
        Console.WriteLine("    · 签名放在 OnSendingRequest 钩子里：request.Content 就是最终上送的字节，不必自己预序列化请求体");
        Console.WriteLine("    · 钩子每次尝试各触发一次，重试会重新签名（时间戳 / nonce 随之更新）");
        Console.WriteLine("    · 防重放两道闸：时间戳窗口（容忍时钟偏差）+ nonce 一次性缓存");
        Console.WriteLine("    · 服务端用 CryptographicOperations.FixedTimeEquals 做定长时间比较，且签名同时绑定令牌——换令牌签名即失效");
    }

    /// <summary>手工构造签名的「异常场景」用：模型里显式写了这些头时，钩子不再覆盖。</summary>
    private static void SetSignatureHeaders(AxiosConfig config, string token, string timestamp, string nonce, string signature)
    {
        config.Headers.Set("Authorization", "Bearer " + token);
        config.Headers.Set("X-Timestamp", timestamp);
        config.Headers.Set("X-Nonce", nonce);
        config.Headers.Set("X-Signature", signature);
    }

    /// <summary>对最终请求消息签名：已带 <c>X-Signature</c> 的请求跳过（异常场景手工构造）。</summary>
    private static async ValueTask SignAsync(
        HttpRequestMessage request,
        string secret,
        string token,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Contains("X-Signature"))
        {
            return;
        }

        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        var signature = RequestSigner.Sign(secret, token, request.Method.Method, pathAndQuery, body, timestamp, nonce);

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        Console.WriteLine($"      [签名] {request.Method.Method} {pathAndQuery} bodyHash={RequestSigner.BodyHash(body)[..12]}…");
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
    public static string BodyHash(ReadOnlySpan<byte> body)
        => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>计算签名，规范串：<c>token \n timestamp \n nonce \n METHOD \n path?query \n sha256hex(body)</c>。</summary>
    public static string Sign(
        string secretHex,
        string token,
        string method,
        string pathAndQuery,
        ReadOnlySpan<byte> body,
        string timestamp,
        string nonce)
    {
        var canonical = string.Join('\n',
            token, timestamp, nonce, method.ToUpperInvariant(), pathAndQuery, BodyHash(body));

        using var hmac = new HMACSHA256(Convert.FromHexString(secretHex));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
