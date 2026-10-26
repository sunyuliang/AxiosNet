using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 基准专用的最小 HTTP/1.1 回环服务器：一条连接上「读一个请求头块（顺带跳掉正文）→ 写一份预编码响应」，
/// Keep-Alive 复用连接。
/// <para>
/// 为什么需要它：真实往返里客户端只占一部分时间，服务器侧的每请求成本必须低到可以当背景，
/// 否则两侧（AxiosNet 与 HttpClient 基线）会被同一个瓶颈压成同一个数字，比值恒为 1。
/// 这里把服务器压到「每请求一读一写」，且响应字节<b>只在启动时编码一次</b>。
/// </para>
/// <para>
/// 与 <c>test/AxiosNet.Tests/Infrastructure/PerfHttpServer.cs</c> 同形态，但刻意各自独立：
/// 基准项目不引用测试项目，避免测试基建重构（例如给 TestRequest 加字段）悄悄改变基准口径。
/// 差异只有一处 —— 这里会按 <c>Content-Length</c> 跳过请求正文，因此能测带正文的请求（POST）。
/// </para>
/// <para>
/// 已知边界：只支持 <c>Content-Length</c> 描述的正文；遇到 <c>Transfer-Encoding: chunked</c> 会在
/// 控制台报错并断开连接（而不是把正文当成下一个请求解析，静默把数字搞脏）。
/// </para>
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly byte[] _response;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private long _requests;

    private LoopbackHttpServer(TcpListener listener, byte[] body, string contentType)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _response = BuildResponse(body, contentType);
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>带结尾斜杠的基地址：与 <see cref="AxiosConfig.BaseAddress"/> 的拼接口径一致。</summary>
    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>累计已回复的请求数（用于确认基准确实打到了服务器，而不是本地短路）。</summary>
    public long Requests => Interlocked.Read(ref _requests);

    public static LoopbackHttpServer Start(byte[] body, string contentType = "application/json; charset=utf-8")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackHttpServer(listener, body, contentType);
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
            var buffer = new byte[BufferSize];
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
                            // 请求头超过 16KB：基准不会发这种请求，直接断开
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

                    var headerLength = headerEnd + 4;
                    var bodyLength = ParseContentLength(buffer, headerEnd);
                    var buffered = filled - headerLength;

                    int consumed;
                    if (buffered >= bodyLength)
                    {
                        // 正文已全部在缓冲区里：其后可能是下一个请求（HTTP/1.1 允许流水线）
                        consumed = headerLength + bodyLength;
                    }
                    else
                    {
                        // 正文还有一部分在流里：读掉丢弃，缓冲区整体消费完
                        await DrainAsync(stream, bodyLength - buffered).ConfigureAwait(false);
                        consumed = filled;
                    }

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
            catch (OperationCanceledException)
            {
                // 窗口结束时的正常收尾
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 客户端提前关闭连接
            }
            catch (Exception ex)
            {
                // 其余异常（例如 chunked 正文）必须可见：静默吞掉会让基准数字悄悄失真
                Console.Error.WriteLine($"[LoopbackHttpServer] 连接处理失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static async Task DrainAsync(NetworkStream stream, int count)
    {
        var scratch = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(count, scratch.Length)))
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("请求正文尚未读完连接就关闭了。");
            }

            count -= read;
        }
    }

    /// <summary>
    /// 从请求头块里取 <c>Content-Length</c>（没有则 0）；<c>chunked</c> 直接拒绝。
    /// <para>
    /// 纯字节扫描，不 <c>GetString</c> + <c>Split</c>：服务器与客户端在同一个进程里，
    /// 每请求几百字节的字符串分配会变成被测数字旁边的 GC 压力。这里只找两个字面量
    /// （<c>content-length:</c> / <c>chunked</c>），足够覆盖基准自己发出的请求。
    /// </para>
    /// </summary>
    private static int ParseContentLength(byte[] buffer, int headerEnd)
    {
        var span = buffer.AsSpan(0, headerEnd);

        if (IndexOfAscii(span, "transfer-encoding"u8) >= 0 && IndexOfAscii(span, "chunked"u8) >= 0)
        {
            throw new NotSupportedException(
                "基准服务器不支持 chunked 请求正文：请让被测请求带 Content-Length（JSON / 表单编码都是）。");
        }

        var at = IndexOfAscii(span, "content-length:"u8);
        if (at < 0)
        {
            return 0;
        }

        var index = at + "content-length:"u8.Length;
        while (index < span.Length && span[index] == (byte)' ')
        {
            index++;
        }

        var value = 0;
        var digits = 0;
        while (index < span.Length && span[index] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (span[index] - '0');
            index++;
            digits++;
        }

        return digits > 0 ? value : 0;
    }

    /// <summary>ASCII 大小写不敏感的 <c>IndexOf</c>（头名不区分大小写）。</summary>
    private static int IndexOfAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (Fold(haystack[i + j]) != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static byte Fold(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

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
