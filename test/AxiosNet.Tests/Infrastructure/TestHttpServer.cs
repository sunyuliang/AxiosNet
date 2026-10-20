using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AxiosNet.Tests.Infrastructure;

/// <summary>测试服务器收到的单个 HTTP 请求。</summary>
public sealed record TestRequest(
    string Method,
    string RawUrl,
    string Path,
    string? Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    int RequestNumberOnConnection,
    string Version = "HTTP/1.1");

/// <summary>测试服务器返回的 HTTP 响应。</summary>
public sealed record TestResponse(
    int StatusCode = 200,
    string? ReasonPhrase = null,
    byte[]? Body = null,
    string ContentType = "application/json; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null,
    bool CloseConnection = false,
    long? ContentLengthOverride = null,
    TimeSpan? BodyDelay = null,
    bool Chunked = false);

/// <summary>
/// 基于 TcpListener 的极简 HTTP/1.1 测试服务器：
/// - 支持 Keep-Alive（同一 TCP 连接上处理多个请求，用于验证连接池的连接复用）；
/// - 统计总连接数、每连接请求数、峰值并发请求数（用于验证最大连接数限制）；
/// - 完整记录收到的请求，便于断言 method/path/query/header/body。
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private static readonly Dictionary<int, string> ReasonPhrases = new()
    {
        [200] = "OK",
        [201] = "Created",
        [204] = "No Content",
        [302] = "Found",
        [400] = "Bad Request",
        [404] = "Not Found",
        [500] = "Internal Server Error"
    };

    private readonly TcpListener _listener;
    private readonly Func<TestRequest, Task<TestResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentQueue<TestRequest> _received = new();
    private readonly ConcurrentDictionary<long, int> _requestsPerConnection = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();

    private int _totalConnections;
    private int _activeRequests;
    private int _peakRequests;
    private long _nextConnectionId;

    private TestHttpServer(TcpListener listener, Func<TestRequest, Task<TestResponse>> handler)
    {
        _listener = listener;
        _handler = handler;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public IReadOnlyList<TestRequest> Received
    {
        get
        {
            var snapshot = new List<TestRequest>();
            foreach (var request in _received)
            {
                snapshot.Add(request);
            }

            return snapshot;
        }
    }

    /// <summary>累计接受的 TCP 连接数。</summary>
    public int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>同时处理中的请求峰值。</summary>
    public int PeakConcurrentRequests => Volatile.Read(ref _peakRequests);

    /// <summary>每条 TCP 连接上分别处理了多少个请求（连接存活期间也可观测）。</summary>
    public IReadOnlyDictionary<long, int> RequestsPerConnection
        => new Dictionary<long, int>(_requestsPerConnection);

    public static TestHttpServer Start(Func<TestRequest, TestResponse> handler)
        => Start(req => Task.FromResult(handler(req)));

    public static TestHttpServer Start(Func<TestRequest, Task<TestResponse>> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return new TestHttpServer(listener, handler);
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
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 10_000;
            client.SendTimeout = 10_000;

            await using var stream = client.GetStream();
            var connectionId = Interlocked.Increment(ref _nextConnectionId);
            var requestsOnConnection = 0;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var headerBytes = await ReadHeadersAsync(stream, _cts.Token);
                    if (headerBytes is null)
                    {
                        break;
                    }

                    requestsOnConnection++;
                    _requestsPerConnection.AddOrUpdate(connectionId, 1, (_, count) => count + 1);
                    var headerText = Encoding.ASCII.GetString(headerBytes);
                    var lines = headerText
                        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

                    var requestLine = lines[0].Split(' ');
                    var method = requestLine[0];
                    var rawUrl = requestLine[1];
                    var version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1";
                    var (path, query) = SplitUrl(rawUrl);
                    var headers = ParseHeaders(lines);

                    // 必须在读取请求体之前回复 100-continue，否则客户端会等待
                    if (headers.TryGetValue("expect", out var expect)
                        && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                    }

                    var body = await ReadBodyAsync(stream, headers, _cts.Token);

                    var testRequest = new TestRequest(
                        method, rawUrl, path, query, headers, body, requestsOnConnection, version);
                    _received.Enqueue(testRequest);

                    var active = Interlocked.Increment(ref _activeRequests);
                    UpdatePeak(active);

                    TestResponse response;
                    try
                    {
                        response = await _handler(testRequest);
                    }
                    catch (Exception ex)
                    {
                        response = new TestResponse(
                            500,
                            "Internal Server Error",
                            Encoding.UTF8.GetBytes(ex.ToString()),
                            "text/plain; charset=utf-8");
                    }

                    try
                    {
                        await WriteResponseAsync(stream, method, response);
                    }
                    catch
                    {
                        // 客户端可能已提前关闭（超时/取消测试）
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeRequests);
                    }

                    var close = response.CloseConnection
                        || (headers.TryGetValue("connection", out var connection)
                            && connection.Contains("close", StringComparison.OrdinalIgnoreCase));
                    if (close)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 空闲读超时 / 连接重置等，连接结束即可
            }
        }
    }

    private static async Task<byte[]?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(1024);
        var one = new byte[1];
        var state = 0; // 匹配 \r \n \r \n

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, cancellationToken);
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
            var ch = (char)one[0];

            state = (state, ch) switch
            {
                (0, '\r') => 1,
                (1, '\n') => 2,
                (2, '\r') => 3,
                (3, '\n') => 4,
                (_, '\r') => 1,
                _ => 0
            };

            if (state == 4)
            {
                return buffer.ToArray();
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseHeaders(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = value;
        }

        return headers;
    }

    private static (string Path, string? Query) SplitUrl(string rawUrl)
    {
        var q = rawUrl.IndexOf('?');
        return q < 0 ? (rawUrl, null) : (rawUrl[..q], rawUrl[(q + 1)..]);
    }

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (!headers.TryGetValue("content-length", out var lengthText)
            || !long.TryParse(lengthText, out var length)
            || length <= 0)
        {
            return Array.Empty<byte>();
        }

        var body = new byte[length];
        var offset = 0;
        while (offset < body.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return body;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string requestMethod, TestResponse response)
    {
        var body = response.Body ?? Array.Empty<byte>();
        var declaredLength = response.ContentLengthOverride ?? body.Length;
        var reason = response.ReasonPhrase
            ?? (ReasonPhrases.TryGetValue(response.StatusCode, out var phrase) ? phrase : "OK");

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n");

        // Chunked：不声明长度，用于验证「服务端未声明 Content-Length 时」的路径。
        if (response.Chunked)
        {
            sb.Append("Transfer-Encoding: chunked\r\n");
        }
        else
        {
            sb.Append("Content-Length: ").Append(declaredLength).Append("\r\n");
        }

        sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");

        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        sb.Append("Connection: ").Append(response.CloseConnection ? "close" : "keep-alive").Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
        await stream.FlushAsync();

        // 先发响应头再延迟正文：用于验证流式响应的超时是否覆盖正文读取阶段。
        if (response.BodyDelay is TimeSpan delay && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        // HEAD / 204 响应不能携带消息体（避免残留字节破坏 Keep-Alive 上的下一次请求解析）
        if (body.Length > 0
            && !string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
            && response.StatusCode != 204)
        {
            if (response.Chunked)
            {
                await WriteChunkedBodyAsync(stream, body);
            }
            else
            {
                await stream.WriteAsync(body);
            }
        }

        await stream.FlushAsync();
    }

    private static async Task WriteChunkedBodyAsync(NetworkStream stream, byte[] body)
    {
        const int chunkSize = 4096;
        for (var offset = 0; offset < body.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, body.Length - offset);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("X") + "\r\n"));
            await stream.WriteAsync(body.AsMemory(offset, count));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
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

    public ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        return ValueTask.CompletedTask;
    }
}
