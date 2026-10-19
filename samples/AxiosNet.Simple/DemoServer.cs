using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

    private DemoServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

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
                        var (status, payload, contentType) = Route(method, path, query, headers, body);
                        await WriteResponseAsync(stream, status, payload, contentType);
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

    private static (int Status, byte[] Body, string ContentType) Route(
        string method,
        string path,
        string query,
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

            case "GET" when path == "/users":
                return Json(200, """
                    [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
                    """);

            case "GET" when path == "/users/1":
                return Json(200, UserJson());

            case "GET" when path == "/search":
                return Json(200, $"{{\"query\":\"{Escape(query)}\"}}");

            case "GET" when path == "/secure":
                return headers.TryGetValue("authorization", out var auth)
                    ? Json(200, $"{{\"authenticated\":true,\"authorization\":\"{Escape(auth)}\"}}")
                    : (401, Encoding.UTF8.GetBytes("""{"message":"unauthorized"}"""), "application/json; charset=utf-8");

            case "GET" when path == "/not-found":
                return Json(404, """{"message":"resource not found"}""");

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
                return (200, body, contentType);

            case "POST" when path == "/form":
                var pairs = Encoding.UTF8.GetString(body)
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('='))
                    .Select(p => $"\"{Uri.UnescapeDataString(p[0])}\":\"{(p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "")}\"");
                return Json(200, "{" + string.Join(",", pairs) + "}");

            case "POST" when path == "/upload":
                // 粗略统计 multipart 部件：本示例只演示发送，不解析文件内容
                var multipartText = Encoding.UTF8.GetString(body);
                var parts = CountOccurrences(multipartText, "Content-Disposition");
                var files = CountOccurrences(multipartText, "filename=");
                return Json(200, $"{{\"parts\":{parts},\"files\":{files},\"bytes\":{body.Length}}}");

            default:
                return Json(404, """{"message":"unknown route"}""");
        }
    }

    private static (int, byte[], string) Json(int status, string json)
        => (status, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task WriteResponseAsync(NetworkStream stream, int status, byte[] body, string contentType)
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
