using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 性能用例专用的最小 HTTP/1.1 回环服务器：只做「一次读一整块请求头 → 写一份预编码响应」，Keep-Alive 复用连接。
/// <para>
/// 为什么不复用 <see cref="TestHttpServer"/>：那台服务器为满足断言精度，逐个字节 <c>ReadAsync</c> 读请求头、
/// 把每个请求包成 <see cref="TestRequest"/> 入队、逐条连接维护计数。每秒数万请求时这些工作会成为真正的瓶颈
/// —— 两侧客户端被服务器压成同一个吞吐数字，比值恒等于 1，既测不出客户端差异、也测不出退化。
/// 这里把服务器成本压到「每请求一读一写」，让它退居为背景，比较的才是两个客户端的开销。
/// </para>
/// <para>
/// 只支持无正文的请求（GET/HEAD 语义），性能用例不需要请求体。
/// </para>
/// </summary>
public sealed class PerfHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;

    private static readonly byte[] DefaultBody = Encoding.UTF8.GetBytes("""{"id":1,"name":"axios-net"}""");

    private readonly TcpListener _listener;
    private readonly byte[] _response;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private long _requests;

    private PerfHttpServer(TcpListener listener, byte[] body, string contentType)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _response = BuildResponse(body, contentType);
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>累计已回复的请求数（用于断言压测确实打到了服务器）。</summary>
    public long Requests => Interlocked.Read(ref _requests);

    public static PerfHttpServer Start(byte[]? body = null, string contentType = "application/json; charset=utf-8")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new PerfHttpServer(listener, body ?? DefaultBody, contentType);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _clients.Add(client);
            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            // 与客户端侧对称：关掉 Nagle，避免 40ms 延迟把吞吐压成与实现无关的常数
            client.NoDelay = true;

            await using var stream = client.GetStream();
            var buffer = new byte[MaxHeaderBytes];
            var filled = 0;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var headerEnd = IndexOfHeaderEnd(buffer, filled);
                    while (headerEnd < 0)
                    {
                        if (filled == buffer.Length)
                        {
                            // 请求头超过 8KB：性能用例不会发这种请求，直接断开
                            return;
                        }

                        var read = await stream.ReadAsync(buffer.AsMemory(filled), _cts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        filled += read;
                        headerEnd = IndexOfHeaderEnd(buffer, filled);
                    }

                    // 把 \r\n\r\n 之后残留的字节前移：HTTP/1.1 允许客户端在同一连接上连续发请求
                    var consumed = headerEnd + 4;
                    var leftover = filled - consumed;
                    if (leftover > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
                    }

                    filled = leftover;

                    await stream.WriteAsync(_response, _cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _requests);
                }
            }
            catch
            {
                // 窗口结束时的连接回收 / 客户端提前关闭，都属正常
            }
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r'
                && buffer[i + 1] == (byte)'\n'
                && buffer[i + 2] == (byte)'\r'
                && buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] BuildResponse(byte[] body, string contentType)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + body.Length.ToString() + "\r\n"
            + "Connection: keep-alive\r\n"
            + "\r\n");

        var response = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, response, 0, head.Length);
        Buffer.BlockCopy(body, 0, response, head.Length, body.Length);
        return response;
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
