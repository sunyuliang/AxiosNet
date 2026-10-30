using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AxiosNet.Simple;

/// <summary>
/// 示例专用的极简 HTTP/1.1 Keep-Alive 服务器（TcpListener 实现，零外部依赖，离线可运行）。
/// 支持连接复用统计，用于演示 AxiosNet 连接池的真实 TCP 复用效果。
/// </summary>
public sealed class DemoServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();

    private int _totalConnections;
    private int _activeRequests;
    private int _peakRequests;

    // 缓存示例用：每次真正被请求到就加一，命中缓存时客户端看不到增长。
    private int _cacheHits;

    /// <summary><c>/flaky</c> 的调用计数：按 tag 分别累计（演示重试用：前 N 次失败，之后成功）。</summary>
    private readonly ConcurrentDictionary<string, int> _flakyAttempts = new(StringComparer.Ordinal);

    /// <summary>取查询串里某个键的值（不处理转义：示例里的值都是 ASCII 简单值）。</summary>
    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], key, StringComparison.OrdinalIgnoreCase))
            {
                return pair[(eq + 1)..];
            }
        }

        return null;
    }

    private DemoServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>令牌 / 请求签名业务逻辑（Bearer 认证与请求签名两个示例的服务端）。</summary>
    internal DemoAuthApi AuthApi { get; } = new();

    public int TotalConnections => Volatile.Read(ref _totalConnections);

    public int PeakConcurrentRequests => Volatile.Read(ref _peakRequests);

    public static DemoServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new DemoServer(listener);
    }

    public void ResetStats()
    {
        Volatile.Write(ref _totalConnections, 0);
        Volatile.Write(ref _activeRequests, 0);
        Volatile.Write(ref _peakRequests, 0);
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

            Interlocked.Increment(ref _totalConnections);
            _clients.Add(client);
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
                    var headerBytes = await ReadUntilAsync(stream, "\r\n\r\n"u8.ToArray(), _cts.Token);
                    if (headerBytes is null)
                    {
                        break;
                    }

                    var headerText = Encoding.ASCII.GetString(headerBytes);
                    var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var parts = lines[0].Split(' ');
                    var method = parts[0];
                    var rawUrl = parts[1];
                    // 请求行版本（HTTP/1.1 等）：/protocol 端点回显它，用于演示 HTTP/2 协商与降级
                    var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
                    var path = rawUrl.Contains('?') ? rawUrl[..rawUrl.IndexOf('?')] : rawUrl;
                    var query = rawUrl.Contains('?') ? rawUrl[(rawUrl.IndexOf('?') + 1)..] : string.Empty;

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
                    if (headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var len) && len > 0)
                    {
                        body = new byte[len];
                        var offset = 0;
                        while (offset < len)
                        {
                            var read = await stream.ReadAsync(body.AsMemory(offset), _cts.Token);
                            if (read == 0)
                            {
                                break;
                            }

                            offset += read;
                        }
                    }

                    var active = Interlocked.Increment(ref _activeRequests);
                    UpdatePeak(active);
                    try
                    {
                        var (status, payload, contentType, contentDisposition, location, extraHeaders) = Route(method, path, query, version, headers, body);
                        await WriteResponseAsync(stream, status, payload, contentType, contentDisposition, location, extraHeaders);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeRequests);
                    }
                }
            }
            catch
            {
                // 连接关闭即结束
            }
        }
    }

    private (int Status, byte[] Body, string ContentType, string? ContentDisposition, string? Location, string? ExtraHeaders) Route(
        string method,
        string path,
        string query,
        string version,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        string UserJson() =>
            """
            {"id":1,"name":"alice","age":18,"active":true,"score":95.5,"address":{"city":"Shanghai","zip":"200000"},"roles":["admin","user"]}
            """;

        switch (method)
        {
            case "GET" when path == "/":
                return Json(200, """{"ok":true,"service":"AxiosNet.Simple.DemoServer"}""");

            case "GET" when path == "/whoami":
                // 回报当前实例的端口：DNS 切换示例靠它判断这条请求落在了哪个「IP」上
                return Json(200, $"{{\"port\":{Port}}}");

            case "GET" when path == "/protocol":
                // 回显请求行里的协议版本：本服务器只实现 HTTP/1.1，
                // 客户端默认请求 HTTP/2 时这里应始终看到 HTTP/1.1（协商失败自动降级的结果）
                return Json(200, $"{{\"requestVersion\":\"{Escape(version)}\",\"serverVersion\":\"HTTP/1.1\"}}");

            case "GET" when path == "/users":
                return Json(200, """
                    [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
                    """);

            case "GET" when path == "/users/1":
                return Json(200, UserJson());

            case "GET" when path == "/cache/counter":
                // 每次真正到达服务端都递增：客户端命中缓存时这个数不会变。
                // 带 ms= 时故意慢一点，用来看「同键并发只发一次」。
                if (query.Contains("ms=") && int.TryParse(query["ms=".Length..], out var cacheDelay) && cacheDelay > 0)
                {
                    Thread.Sleep(cacheDelay);
                }

                return Json(200, $"{{\"served\":{Interlocked.Increment(ref _cacheHits)}}}");

            case "GET" when path == "/cache/ttl":
            {
                // 响应自己声明新鲜期：FreshnessMode = Server / Intersection 时客户端按它算 TTL。
                // ?max-age=N 自定义新鲜期（默认 2 秒），?no-store=1 声明不许缓存。
                var maxAge = 2;
                if (QueryValue(query, "max-age") is { } maxAgeText && int.TryParse(maxAgeText, out var parsedMaxAge))
                {
                    maxAge = parsedMaxAge;
                }

                var cacheControl = QueryValue(query, "no-store") == "1"
                    ? "no-store"
                    : $"public, max-age={maxAge}";

                return Json(
                    200,
                    $"{{\"served\":{Interlocked.Increment(ref _cacheHits)}}}",
                    $"Cache-Control: {cacheControl}\r\n");
            }

            case "GET" when path == "/cache/etag":
            {
                // 条件请求演示：带匹配的 If-None-Match 就回 304（没有正文），否则回 200 + ETag。
                // max-age=1 让条目很快变旧，客户端才会拿校验器重新验证。
                if (headers.TryGetValue("if-none-match", out var noneMatch) && noneMatch == "\"v1\"")
                {
                    return (304, Array.Empty<byte>(), "application/json; charset=utf-8", null, null,
                        "ETag: \"v1\"\r\nCache-Control: public, max-age=1\r\n");
                }

                return Json(
                    200,
                    $"{{\"served\":{Interlocked.Increment(ref _cacheHits)}}}",
                    "ETag: \"v1\"\r\nCache-Control: public, max-age=1\r\n");
            }

            case "GET" when path == "/users/1/pascal":
                // PascalCase 契约：属性名保持原样，用于演示自定义 JSON 序列化器
                return Json(200, """
                    {"UserName":"bob","Age":30,"Address":{"City":"Shanghai","Zip":"200000"},"Roles":["admin","user"]}
                    """);

            case "GET" when path == "/users/1/xml":
                // XML 契约：演示 IContentSerializer 接入 JSON 之外的格式（正文由序列化器产出/解析）
                return (200,
                    Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="utf-8"?><User><Id>1</Id><Name>alice</Name><Age>30</Age></User>"""),
                    "application/xml; charset=utf-8",
                    null,
                    null,
                    null);

            case "GET" when path == "/orders/1/binary":
                // 二进制契约：Content-Type 非文本 → 库不解码，原始字节直达序列化器的字节入口
                return (200,
                    OrderBinarySerializer.Write(new BinaryOrder { Id = 1, Name = "alice", Quantity = 2, AmountCents = 1999 }),
                    "application/octet-stream",
                    null,
                    null,
                    null);

            case "GET" when path == "/search":
                return Json(200, $"{{\"query\":\"{Escape(query)}\"}}");

            case "GET" when path == "/secure":
                return headers.TryGetValue("authorization", out var auth)
                    ? Json(200, $"{{\"authenticated\":true,\"authorization\":\"{Escape(auth)}\"}}")
                    : (401, Encoding.UTF8.GetBytes("""{"message":"unauthorized"}"""), "application/json; charset=utf-8", null, null, null);

            case "GET" when path == "/not-found":
                return Json(404, """{"message":"resource not found"}""");

            case "GET" or "POST" when path == "/flaky":
            {
                // ?fail=N：同一个 tag 的前 N 次调用返回 503，之后成功（演示弹性重试）
                // POST 也收：幂等键示例演示的是「同一个键的多次写只算一次」。
                var failures = 2;
                if (QueryValue(query, "fail") is { } failText && int.TryParse(failText, out var parsedFailures))
                {
                    // 仅在解析成功时采用该值（失败时 out 参数为 0）
                    failures = parsedFailures;
                }

                var tag = QueryValue(query, "tag") ?? "default";
                var attempt = _flakyAttempts.AddOrUpdate(tag, 1, (_, current) => current + 1);
                return attempt <= failures
                    ? Json(503, $"{{\"message\":\"temporarily unavailable\",\"attempt\":{attempt}}}")
                    : Json(200, $"{{\"ok\":true,\"attempt\":{attempt}}}");
            }

            case "GET" when path == "/slow":
                var ms = 500;
                if (query.Contains("ms="))
                {
                    var raw = query["ms=".Length..];
                    int.TryParse(raw, out ms);
                }

                Thread.Sleep(ms);
                return Json(200, $"{{\"delayedMs\":{ms}}}");

            case "POST" when path == "/echo":
                var contentType = headers.GetValueOrDefault("content-type", "application/json");
                return (200, body, contentType, null, null, null);

            case "GET" when path == "/download":
            {
                // 二进制内容，并由服务端通过 Content-Disposition 指定保存文件名；?kb= 控制大小
                var kb = 1024;
                if (query.Contains("kb="))
                {
                    int.TryParse(query["kb=".Length..], out kb);
                }

                var file = new byte[Math.Max(kb, 1) * 1024];
                for (var i = 0; i < file.Length; i++)
                {
                    file[i] = (byte)(i % 251);
                }

                return (200, file, "application/octet-stream", "attachment; filename=\"sample-report.bin\"", null, null);
            }

            case "GET" when path == "/big":
            {
                // 8 MB：用于演示 maxContentLength 拦截
                var file = new byte[8 * 1024 * 1024];
                return (200, file, "application/octet-stream", null, null, null);
            }

            case "GET" when path == "/redirect":
                // 302：用于演示 maxRedirects / 是否跟随重定向
                return (302, Array.Empty<byte>(), "text/plain", null, "/users/1", null);

            case "GET" when path == "/headers":
            {
                // 回显全部请求头（键统一小写）：用于演示自定义 Handler 给请求加头
                var echoed = headers.Select(kv =>
                    $"\"{Escape(kv.Key.ToLowerInvariant())}\":\"{Escape(kv.Value)}\"");
                return Json(200, "{" + string.Join(",", echoed) + "}");
            }

            case "PROPFIND" when path == "/users/1":
                // 非标准方法：用于演示直接用 BCL HttpMethod 发自定义谓词
                return Json(200, """{"method":"PROPFIND","supported":true}""");

            case "POST" when path == "/form":
                var pairs = Encoding.UTF8.GetString(body)
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('='))
                    .Select(p => $"\"{Uri.UnescapeDataString(p[0])}\":\"{(p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "")}\"");
                return Json(200, "{" + string.Join(",", pairs) + "}");

            case "POST" when path == "/upload":
            {
                // 精确解析 multipart：逐部件解析 name/filename/content-type，并按字节截取内容
                if (!MultipartReader.TryParse(
                        headers.GetValueOrDefault("content-type"), body, out var parts, out var parseError))
                {
                    Console.Error.WriteLine($"[multipart] {parseError} | ct={headers.GetValueOrDefault("content-type")} | len={body.Length}");
                    return Json(400, $"{{\"error\":\"{Escape(parseError!)}\"}}");
                }

                var fileParts = parts.Where(p => p.IsFile).ToArray();
                var fileBytes = fileParts.Sum(p => (long)p.Content.Length);

                return Json(200,
                    $"{{\"parts\":{parts.Count},\"files\":{fileParts.Length},\"bytes\":{fileBytes},"
                    + $"\"partsDetail\":[{string.Join(",", parts.Select(DescribePart))}]}}");
            }

            case "POST" when path == "/upload/single":
            {
                // 单文件（multipart）：取出文件部件 + 可选普通字段，回报精确字节数与 SHA256
                if (!MultipartReader.TryParse(
                        headers.GetValueOrDefault("content-type"), body, out var parts, out var parseError))
                {
                    return Json(400, $"{{\"error\":\"{Escape(parseError!)}\"}}");
                }

                var file = parts.FirstOrDefault(p => p.IsFile);
                if (file is null)
                {
                    return Json(400, """{"error":"没有找到文件部件（缺少 filename）"}""");
                }

                var note = parts.FirstOrDefault(p => !p.IsFile && p.Name == "note")?.Text;

                return Json(200,
                    $"{{\"mode\":\"multipart\",\"field\":\"{Escape(file.Name)}\","
                    + $"\"fileName\":\"{Escape(file.FileName!)}\","
                    + $"\"contentType\":\"{Escape(file.ContentType ?? string.Empty)}\","
                    + $"\"bytes\":{file.Content.Length},\"sha256\":\"{Sha256Hex(file.Content)}\","
                    + $"\"note\":\"{Escape(note ?? string.Empty)}\"}}");
            }

            case "POST" when path == "/upload/raw":
            {
                // 裸流直传：整个请求体就是文件内容，文件名/类型只能从头部拿
                var fileName = headers.GetValueOrDefault("x-file-name", "upload.bin");

                return Json(200,
                    $"{{\"mode\":\"raw\",\"fileName\":\"{Escape(fileName)}\","
                    + $"\"bytes\":{body.Length},"
                    + $"\"contentType\":\"{Escape(headers.GetValueOrDefault("content-type", string.Empty))}\","
                    + $"\"sha256\":\"{Sha256Hex(body)}\"}}");
            }

            // ---- Bearer 令牌 / 请求签名（业务逻辑在 DemoAuthApi） ----
            case "POST" when path == DemoAuthApi.LoginPath:
                return Json(AuthApi.Login(query, Encoding.UTF8.GetString(body)));

            case "GET" when path == DemoAuthApi.VerifyPath:
                return Json(AuthApi.VerifyToken(headers));

            case "GET" when path == DemoAuthApi.OrdersPath:
                return Json(AuthApi.Orders(headers));

            case "POST" when path == DemoAuthApi.RefreshPath:
                return Json(AuthApi.Refresh(Encoding.UTF8.GetString(body)));

            case "POST" when path == DemoAuthApi.LogoutPath:
                return Json(AuthApi.Logout(headers));

            case "POST" when path == DemoAuthApi.SignedOrdersPath:
            case "GET" when path == DemoAuthApi.SignedProfilePath:
                return Json(AuthApi.VerifySigned(method, path, query, headers, body));

            default:
                return Json(404, """{"message":"unknown route"}""");
        }
    }

    private static (int, byte[], string, string?, string?, string?) Json(int status, string json, string? extraHeaders = null)
        => (status, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", null, null, extraHeaders);

    /// <summary>DemoAuthApi 返回 (状态码, JSON 文本)，转成路由元组。</summary>
    private static (int, byte[], string, string?, string?, string?) Json((int Status, string Json) result)
        => Json(result.Status, result.Json);

    /// <summary>把解析出的部件序列化成一段 JSON（用于 /upload 的明细）。</summary>
    private static string DescribePart(MultipartPart part)
    {
        var fileName = part.FileName is null ? "null" : $"\"{Escape(part.FileName)}\"";
        var contentType = part.ContentType is null ? "null" : $"\"{Escape(part.ContentType)}\"";

        return $"{{\"field\":\"{Escape(part.Name)}\",\"fileName\":{fileName},"
            + $"\"contentType\":{contentType},\"bytes\":{part.Content.Length},"
            + $"\"sha256\":\"{Sha256Hex(part.Content)}\"}}";
    }

    private static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        byte[] body,
        string contentType,
        string? contentDisposition,
        string? location,
        string? extraHeaders)
    {
        var reason = status switch
        {
            200 => "OK",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            (contentDisposition is null ? string.Empty : $"Content-Disposition: {contentDisposition}\r\n") +
            (location is null ? string.Empty : $"Location: {location}\r\n") +
            (extraHeaders ?? string.Empty) +
            "Connection: keep-alive\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<byte[]?> ReadUntilAsync(NetworkStream stream, byte[] terminator, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        while (matched < terminator.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            buffer.WriteByte(one[0]);
            matched = one[0] == terminator[matched] ? matched + 1 : (one[0] == terminator[0] ? 1 : 0);
        }

        return buffer.ToArray();
    }

    private void UpdatePeak(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _peakRequests);
            if (active <= current || Interlocked.CompareExchange(ref _peakRequests, active, current) == current)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }
}
