using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxiosNet.Simple.WebAPI;

/// <summary>
/// 示例专用的极简 HTTP/1.1 Keep-Alive 服务器（TcpListener 实现，零外部依赖，离线可运行）。
/// 提供一组典型的 REST 端点：列表（含过滤）、详情、创建、删除、登录、受保护资源，
/// 供 5 个声明式示例做真实 HTTP 往返。
/// </summary>
public sealed class DemoApiServer : IDisposable
{
    private static readonly UserProfile[] Users =
    [
        new(1, "alice", 18, "admin"),
        new(2, "bob", 30, "dev")
    ];

    /// <summary>受保护端点接受的固定演示令牌（真实系统里由 /api/auth/login 签发并带过期时间）。</summary>
    public const string DemoToken = "demo-token-2026";

    /// <summary>签名端点共享密钥（真实系统里按 app 预共享，不会随请求下发）。</summary>
    public const string SigningKey = "sample-signing-key-2026";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private DemoApiServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public static DemoApiServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new DemoApiServer(listener);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var (method, path, query, headers, body) = await ReadRequestAsync(stream);
                    if (method is null)
                    {
                        break;
                    }

                    var (status, json, extraHeaders) = Route(method, path, query, headers, body);
                    await WriteResponseAsync(stream, status, json, extraHeaders);
                }
            }
            catch
            {
                // 连接关闭即结束
            }
        }
    }

    private static async Task<(string? Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body)>
        ReadRequestAsync(NetworkStream stream)
    {
        using var headerBuffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        // 读到 \r\n\r\n 为止（与主库示例服务器同一套逐字节匹配）
        while (matched < 4)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return default;
            }

            if (read == 0)
            {
                return default;
            }

            headerBuffer.WriteByte(one[0]);
            matched = one[0] == "\r\n\r\n"[matched] ? matched + 1 : (one[0] == '\r' ? 1 : 0);
        }

        var lines = Encoding.ASCII.GetString(headerBuffer.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var rawUrl = requestLine[1];
        var queryIndex = rawUrl.IndexOf('?');
        var path = queryIndex < 0 ? rawUrl : rawUrl[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : rawUrl[(queryIndex + 1)..];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("content-length", out var lengthText)
            && int.TryParse(lengthText, out var length)
            && length > 0)
        {
            body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset));
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }
        }

        return (requestLine[0], path, query, headers, body);
    }

    private static (int Status, string Json, Dictionary<string, string>? ExtraHeaders) Route(
        string method,
        string path,
        string query,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        switch (method)
        {
            case "GET" when path == "/api/users":
                return (200, UsersJson(query), null);

            case "GET" when path.StartsWith("/api/users/"):
                return int.TryParse(path["/api/users/".Length..], out var id) && Find(id) is { } user
                    ? (200, JsonSerializer.Serialize(user), null)
                    : (404, """{"error":"user not found"}""", null);

            case "DELETE" when path.StartsWith("/api/users/"):
                return int.TryParse(path["/api/users/".Length..], out var deletedId)
                    ? (200, $$"""{"id":{{deletedId}},"deleted":true}""", null)
                    : (404, """{"error":"user not found"}""", null);

            case "POST" when path == "/api/users":
                return (201, CreateUserJson(headers, body), null);

            case "POST" when path == "/api/auth/login":
                return (200, $$"""{"token":"{{DemoToken}}","expiresIn":300}""", null);

            case "GET" when path == "/api/secure/profile":
                return headers.TryGetValue("authorization", out var auth)
                    && auth == $"Bearer {DemoToken}"
                    ? (200, JsonSerializer.Serialize(new SecureProfile(
                        "alice",
                        "AxiosNet.Simple.WebAPI.DemoApiServer",
                        headers.GetValueOrDefault("x-device", "unknown"))), null)
                    : (401, """{"error":"missing or invalid bearer token"}""", null);

            case "POST" when path == "/api/signed/echo":
                return SignedEcho(headers, body);

            default:
                return (404, """{"error":"no such route"}""", null);
        }
    }

    /// <summary>
    /// 签名端点：验证请求体 HMAC-SHA256（X-Signature，十六进制），有效则回显并给自己的响应也签名
    /// （客户端侧同一位 SigningSerializer 负责响应验签）。
    /// </summary>
    private static (int, string, Dictionary<string, string>) SignedEcho(
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        var expected = headers.GetValueOrDefault("x-signature");

        if (string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(HmacHex(body)),
                Convert.FromHexString(expected)))
        {
            return (401, """{"error":"missing or invalid X-Signature"}""", null!);
        }

        // 请求体经签名校验后才解析：签名是完整性边界
        var payload = JsonSerializer.Deserialize<UserPayload>(body, BodyOptions)!;
        var json = JsonSerializer.Serialize(new SignedEchoResult(true, payload.Name, payload.Age));
        var signature = HmacHex(Encoding.UTF8.GetBytes(json));

        return (200, json, new Dictionary<string, string> { ["X-Signature"] = signature });
    }

    private static string HmacHex(byte[] data)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), data));

    private static UserProfile? Find(int id) => Users.FirstOrDefault(u => u.Id == id) is { } found ? found : null;

    /// <summary>解析 role= 与 ids[]= 过滤参数并返回列表（键名与请求模型的 Query 绑定对应，比较不区分大小写）。</summary>
    private static string UsersJson(string query)
    {
        var pairs = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair))
            .ToArray();

        var role = pairs
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Equals("role", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1])
            .FirstOrDefault();

        var ids = pairs
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2
                && kv[0].Equals("ids[]", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1], out _))
            .Select(kv => int.Parse(kv[1]))
            .ToArray();

        var result = Users
            .Where(u => role is null || u.Role == role)
            .Where(u => ids.Length == 0 || ids.Contains((int)u.Id))
            .ToArray();

        return JsonSerializer.Serialize(result);
    }

    /// <summary>请求体解析用：键名大小写不敏感（AxiosNet 默认按 camelCase 序列化，属性是 PascalCase）。</summary>
    private static readonly JsonSerializerOptions BodyOptions = new() { PropertyNameCaseInsensitive = true };

    private static string CreateUserJson(IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        var payload = JsonSerializer.Deserialize<UserPayload>(body, BodyOptions);

        // 把 X-Trace 一并回显：证明请求模型 Configure 钩子里写的头真实到达了服务器
        var trace = headers.GetValueOrDefault("x-trace", "none");

        return JsonSerializer.Serialize(new
        {
            Id = 3,
            payload!.Name,
            payload.Age,
            Role = "new",
            Trace = trace
        });
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string json,
        Dictionary<string, string>? extraHeaders)
    {
        var reason = status switch
        {
            200 => "OK",
            201 => "Created",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "OK"
        };

        var body = Encoding.UTF8.GetBytes(json);
        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n";

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                header += $"{name}: {value}\r\n";
            }
        }

        header += "Connection: keep-alive\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
