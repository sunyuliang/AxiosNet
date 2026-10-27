using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxiosNet.Simple;

/// <summary>
/// 演示服务端的「Bearer 令牌 + 请求签名」业务逻辑，等价于真实系统里的认证服务 / API 网关。
/// <see cref="DemoServer"/> 只负责 HTTP 收发，具体判定全部委托到这里。
/// </summary>
internal sealed class DemoAuthApi
{
    internal const string LoginPath = "/api/token/login";
    internal const string VerifyPath = "/api/token/verify";
    internal const string RefreshPath = "/api/token/refresh";
    internal const string LogoutPath = "/api/token/logout";
    internal const string OrdersPath = "/api/orders";
    internal const string SignedOrdersPath = "/api/signed/orders";
    internal const string SignedProfilePath = "/api/signed/profile";

    private const string DemoUser = "alice";
    private const string DemoPassword = "wonderland";
    private const int DefaultTokenTtlSeconds = 300;

    /// <summary>签名时间戳允许的偏差窗口（秒）：|服务端时间 - X-Timestamp| 超出即判过期。</summary>
    private const long SignatureWindowSeconds = 300;

    /// <summary>nonce 缓存上限，超过后顺手清理过期项（演示服务器不追求严谨的容量管理）。</summary>
    private const int NonceCacheLimit = 4096;

    private readonly ConcurrentDictionary<string, AccessEntry> _accessTokens = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
    private readonly ConcurrentDictionary<string, long> _nonces = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed record AccessEntry(string SessionId, long ExpiresAtTicks);

    private sealed class Session
    {
        public required string Username { get; init; }

        /// <summary>HMAC 共享密钥（十六进制）。真实系统里按 app 预共享，不会随登录下发。</summary>
        public required string SecretHex { get; init; }

        public volatile bool Revoked;
    }

    /// <summary>签发令牌：账号密码换 access/refresh 令牌 + 会话密钥。?ttl= 可指定过期秒数（演示用）。</summary>
    public (int Status, string Json) Login(string query, string body)
    {
        var ttl = DefaultTokenTtlSeconds;
        if (query.Contains("ttl=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(query[(query.IndexOf("ttl=", StringComparison.OrdinalIgnoreCase) + 4)..], out var parsed))
        {
            ttl = Math.Clamp(parsed, 1, 3600);
        }

        string? username = null;
        string? password = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            username = root.TryGetProperty("username", out var user) ? user.GetString() : null;
            password = root.TryGetProperty("password", out var pass) ? pass.GetString() : null;
        }
        catch (JsonException)
        {
            // 请求体不是合法 JSON：按凭据错误处理
        }

        if (!string.Equals(username, DemoUser, StringComparison.Ordinal)
            || !string.Equals(password, DemoPassword, StringComparison.Ordinal))
        {
            return (401, """{"error":"invalid_credentials"}""");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secretHex = Convert.ToHexString(secretBytes);

        _sessions[sessionId] = new Session { Username = DemoUser, SecretHex = secretHex };

        var accessToken = Guid.NewGuid().ToString("N");
        var refreshToken = Guid.NewGuid().ToString("N");
        _accessTokens[accessToken] = new AccessEntry(sessionId, DateTime.UtcNow.AddSeconds(ttl).Ticks);
        _refreshTokens[refreshToken] = sessionId;

        return (200, JsonSerializer.Serialize(new
        {
            tokenType = "Bearer",
            accessToken,
            refreshToken,
            expiresIn = ttl,
            username = DemoUser,
            appSecret = secretHex.ToLowerInvariant()
        }));
    }

    /// <summary>验证令牌：GET /api/token/verify，校验 Bearer 令牌是否仍有效。</summary>
    public (int Status, string Json) VerifyToken(IReadOnlyDictionary<string, string> headers)
    {
        var (session, error) = ResolveBearer(headers);
        if (error is not null)
        {
            return (401, $"{{\"error\":\"{error}\"}}");
        }

        return (200, JsonSerializer.Serialize(new
        {
            valid = true,
            username = session!.Username
        }));
    }

    /// <summary>受保护资源：GET /api/orders，与验证端点共用同一套令牌校验。</summary>
    public (int Status, string Json) Orders(IReadOnlyDictionary<string, string> headers)
    {
        var (session, error) = ResolveBearer(headers);
        if (error is not null)
        {
            return (401, $"{{\"error\":\"{error}\"}}");
        }

        return (200, JsonSerializer.Serialize(new
        {
            user = session!.Username,
            orders = new[]
            {
                new { id = 1001, item = "机械键盘", amount = 299.00 },
                new { id = 1002, item = "27 寸显示器", amount = 1599.00 }
            }
        }));
    }

    /// <summary>刷新令牌：refresh 一次性轮换，旧 refresh 当即作废。</summary>
    public (int Status, string Json) Refresh(string body)
    {
        string? refreshToken = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            refreshToken = document.RootElement.TryGetProperty("refreshToken", out var token) ? token.GetString() : null;
        }
        catch (JsonException)
        {
        }

        if (refreshToken is null
            || !_refreshTokens.TryRemove(refreshToken, out var sessionId)
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.Revoked)
        {
            return (401, """{"error":"invalid_refresh_token"}""");
        }

        var accessToken = Guid.NewGuid().ToString("N");
        var newRefreshToken = Guid.NewGuid().ToString("N");
        _accessTokens[accessToken] = new AccessEntry(sessionId, DateTime.UtcNow.AddSeconds(DefaultTokenTtlSeconds).Ticks);
        _refreshTokens[newRefreshToken] = sessionId;

        return (200, JsonSerializer.Serialize(new
        {
            tokenType = "Bearer",
            accessToken,
            refreshToken = newRefreshToken,
            expiresIn = DefaultTokenTtlSeconds
        }));
    }

    /// <summary>登出：吊销整个会话（其下全部 access / refresh 令牌一并作废）。</summary>
    public (int Status, string Json) Logout(IReadOnlyDictionary<string, string> headers)
    {
        var (session, error) = ResolveBearer(headers);
        if (error is not null)
        {
            return (401, $"{{\"error\":\"{error}\"}}");
        }

        RevokeSession(session!);
        return (200, """{"revoked":true}""");
    }

    /// <summary>
    /// 签名校验：POST /api/signed/orders、GET /api/signed/profile 共用。
    /// <para>
    /// 规范串（客户端与服务端必须逐字节一致）：
    /// <c>token \n timestamp \n nonce \n METHOD \n path?query \n sha256hex(body)</c>
    /// </para>
    /// </summary>
    public (int Status, string Json) VerifySigned(
        string method,
        string path,
        string query,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        // 1) Bearer 令牌 → 定位会话与共享密钥（签名绑定令牌，换令牌签名即失效）
        var (session, error) = ResolveBearer(headers);
        if (error is not null)
        {
            return (401, $"{{\"error\":\"{error}\"}}");
        }

        // 2) 三个签名头必须齐全
        if (!headers.TryGetValue("x-timestamp", out var timestamp)
            || !headers.TryGetValue("x-nonce", out var nonce)
            || !headers.TryGetValue("x-signature", out var signature))
        {
            return (401, """{"error":"missing_signature_headers"}""");
        }

        // 3) 时间戳窗口：防重放第一道闸（同时也容忍一定时钟偏差）
        if (!long.TryParse(timestamp, out var unixSeconds)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > SignatureWindowSeconds)
        {
            return (401, """{"error":"timestamp_out_of_window"}""");
        }

        // 4) nonce 一次性：防重放第二道闸（同一请求原样重发会被拒绝）
        if (!_nonces.TryAdd(nonce, unixSeconds + SignatureWindowSeconds))
        {
            return (401, """{"error":"nonce_replayed"}""");
        }

        CleanupNonces(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // 5) 重算签名并定长时间比较（避免逐字节比对泄露匹配长度信息）
        var token = headers["authorization"]["Bearer ".Length..].Trim();
        var pathAndQuery = query.Length == 0 ? path : path + "?" + query;
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join('\n', token, timestamp, nonce, method.ToUpperInvariant(), pathAndQuery, bodyHash);

        using var hmac = new HMACSHA256(Convert.FromHexString(session!.SecretHex));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToUpperInvariant())))
        {
            return (401, """{"error":"signature_mismatch"}""");
        }

        return (200, JsonSerializer.Serialize(new
        {
            verified = true,
            user = session.Username,
            path = pathAndQuery,
            bodySha256 = bodyHash
        }));
    }

    /// <summary>解析 Authorization: Bearer &lt;token&gt;，返回 (会话, 错误码)。</summary>
    private (Session? Session, string? Error) ResolveBearer(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("authorization", out var authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "missing_token");
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return (null, "missing_token");
        }

        if (!_accessTokens.TryGetValue(token, out var entry))
        {
            return (null, "unknown_token");
        }

        if (DateTime.UtcNow.Ticks > entry.ExpiresAtTicks)
        {
            return (null, "token_expired");
        }

        if (!_sessions.TryGetValue(entry.SessionId, out var session) || session.Revoked)
        {
            return (null, "session_revoked");
        }

        return (session, null);
    }

    private void RevokeSession(Session session)
    {
        session.Revoked = true;

        var sessionId = _sessions.FirstOrDefault(kv => ReferenceEquals(kv.Value, session)).Key;
        if (sessionId is null)
        {
            return;
        }

        foreach (var entry in _accessTokens)
        {
            if (string.Equals(entry.Value.SessionId, sessionId, StringComparison.Ordinal))
            {
                _accessTokens.TryRemove(entry.Key, out _);
            }
        }

        foreach (var entry in _refreshTokens)
        {
            if (string.Equals(entry.Value, sessionId, StringComparison.Ordinal))
            {
                _refreshTokens.TryRemove(entry.Key, out _);
            }
        }
    }

    private void CleanupNonces(long now)
    {
        if (_nonces.Count <= NonceCacheLimit)
        {
            return;
        }

        foreach (var entry in _nonces)
        {
            if (entry.Value < now)
            {
                _nonces.TryRemove(entry.Key, out _);
            }
        }
    }
}
