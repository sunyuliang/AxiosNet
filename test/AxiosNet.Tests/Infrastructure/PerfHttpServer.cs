using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 性能用例专用的最小 HTTP/1.1 回环服务器：只做「读满一条请求 → 写一份预编码响应」，Keep-Alive 复用连接。
/// <para>
/// 为什么不复用 <see cref="TestHttpServer"/>：那台服务器为满足断言精度，逐个字节 <c>ReadAsync</c> 读请求头、
/// 把每个请求包成 <see cref="TestRequest"/> 入队、逐条连接维护计数。每秒数万请求时这些工作会成为真正的瓶颈
/// —— 两侧客户端被服务器压成同一个吞吐数字，比值恒等于 1，既测不出客户端差异、也测不出退化。
/// 这里把服务器成本压到「每请求一读一写」，让它退居为背景，比较的才是两个客户端的开销。
/// </para>
/// <para>
/// 正文支持两种分帧：<c>Content-Length</c> 已知长度，或 <c>Transfer-Encoding: chunked</c>（.NET 的
/// <c>JsonContent</c> / <c>PostAsJsonAsync</c> 走的就是后者）。两者都没有时按无正文处理，即 GET/HEAD 的同一条路径。
/// </para>
/// </summary>
public sealed class PerfHttpServer : IAsyncDisposable
{
    /// <summary>单条请求（请求头 + 正文）的缓冲上限；性能用例的正文只有几十字节，超出说明请求不合预期，直接断开。</summary>
    private const int MaxRequestBytes = 16 * 1024;

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
            var buffer = new byte[MaxRequestBytes];
            var filled = 0;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var headerEnd = IndexOfHeaderEnd(buffer, filled);
                    while (headerEnd < 0)
                    {
                        filled = await ReadMoreAsync(stream, buffer, filled).ConfigureAwait(false);
                        if (filled < 0)
                        {
                            // 连接已关闭，或请求头超过缓冲上限
                            return;
                        }

                        headerEnd = IndexOfHeaderEnd(buffer, filled);
                    }

                    // 头之后可能跟着正文：读满整条请求再回复，否则正文会被当成下一条请求的头部
                    var measured = await MeasureRequestEndAsync(stream, buffer, filled, headerEnd).ConfigureAwait(false);
                    filled = measured.Filled;
                    if (measured.Total < 0)
                    {
                        return;
                    }

                    var total = measured.Total;

                    // 把本条请求之后残留的字节前移：HTTP/1.1 允许客户端在同一连接上连续发请求
                    var leftover = filled - total;
                    if (leftover > 0)
                    {
                        Buffer.BlockCopy(buffer, total, buffer, 0, leftover);
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

    /// <summary>
    /// 读出当前这条请求的结束位置（请求头之后按分帧方式把正文读完）。
    /// <c>Total</c> 为本条请求的总字节数、<c>Filled</c> 为新的已读字节数；
    /// 连接关闭、分帧畸形或超过缓冲上限时 <c>Total</c> 为 -1。
    /// </summary>
    private async ValueTask<(int Total, int Filled)> MeasureRequestEndAsync(
        NetworkStream stream,
        byte[] buffer,
        int filled,
        int headerEnd)
    {
        // 分帧只在同步代码里解析：async 方法中不能出现 Span 局部变量（C# 12）
        var (chunked, contentLength) = ParseBodyFraming(buffer, headerEnd);
        var bodyStart = headerEnd + 4;

        if (chunked)
        {
            // 分块正文：一直读到末块（0 长度块 + 结束空行）到齐为止
            while (true)
            {
                if (TryMeasureChunkedBody(buffer, filled, bodyStart, out var end))
                {
                    return (end, filled);
                }

                filled = await ReadMoreAsync(stream, buffer, filled).ConfigureAwait(false);
                if (filled < 0)
                {
                    return (-1, 0);
                }
            }
        }

        var total = bodyStart + contentLength;
        while (filled < total)
        {
            filled = await ReadMoreAsync(stream, buffer, filled).ConfigureAwait(false);
            if (filled < 0)
            {
                return (-1, 0);
            }
        }

        return (total, filled);
    }

    /// <summary>解析一条请求的正文分帧：是否 chunked，以及 <c>Content-Length</c>（没有该头时为 0）。</summary>
    private static (bool Chunked, int ContentLength) ParseBodyFraming(byte[] buffer, int headerEnd)
    {
        var headers = buffer.AsSpan(0, headerEnd);
        return (HasChunkedTransferEncoding(headers), ParseContentLength(headers));
    }

    /// <summary>再读一块接到 <paramref name="filled"/> 之后；连接关闭或缓冲已满时返回 -1，否则返回新的已读字节数。</summary>
    private async ValueTask<int> ReadMoreAsync(NetworkStream stream, byte[] buffer, int filled)
    {
        if (filled == buffer.Length)
        {
            return -1;
        }

        var read = await stream.ReadAsync(buffer.AsMemory(filled), _cts.Token).ConfigureAwait(false);
        return read == 0 ? -1 : filled + read;
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

    /// <summary>
    /// 算出 chunked 正文的结束位置：逐块累加「块长行 + 数据 + CRLF」，末块是长度 0 的块，
    /// 之后是可选的 trailer 与一个空行。数据没到齐或分帧畸形时返回 false。
    /// </summary>
    private static bool TryMeasureChunkedBody(byte[] buffer, int length, int bodyStart, out int total)
    {
        var span = buffer.AsSpan(0, length);
        total = 0;
        var index = bodyStart;

        while (true)
        {
            var lineEnd = IndexOfCrlf(span, index);
            if (lineEnd < 0 || !TryParseHex(span[index..lineEnd], out var size))
            {
                return false;
            }

            index = lineEnd + 2;

            if (size == 0)
            {
                // 末块：后面是 trailer 段（通常是空的），以一个空行结束
                while (true)
                {
                    var trailerEnd = IndexOfCrlf(span, index);
                    if (trailerEnd < 0)
                    {
                        return false;
                    }

                    if (trailerEnd == index)
                    {
                        total = index + 2;
                        return true;
                    }

                    index = trailerEnd + 2;
                }
            }

            if (index + size + 2 > span.Length)
            {
                // 数据或块尾 CRLF 还没到齐
                return false;
            }

            if (span[index + size] != (byte)'\r' || span[index + size + 1] != (byte)'\n')
            {
                return false;
            }

            index += size + 2;
        }
    }

    /// <summary>从 <paramref name="start"/> 起找下一个 CRLF，返回 CR 的下标；找不到返回 -1。</summary>
    private static int IndexOfCrlf(ReadOnlySpan<byte> span, int start)
    {
        for (var i = start; i + 1 < span.Length; i++)
        {
            if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>解析块长行里的十六进制长度；允许 <c>;</c> 之后带块扩展，没有十六进制位时返回 false。</summary>
    private static bool TryParseHex(ReadOnlySpan<byte> line, out int value)
    {
        value = 0;
        var digits = 0;

        foreach (var b in line)
        {
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                (byte)';' => -1,
                _ => -2
            };

            if (digit == -1)
            {
                break;
            }

            if (digit < 0)
            {
                return false;
            }

            value = (value * 16) + digit;
            digits++;
        }

        return digits > 0;
    }

    /// <summary>请求头里是否出现 <c>chunked</c>（即 <c>Transfer-Encoding: chunked</c>），大小写不敏感。</summary>
    private static bool HasChunkedTransferEncoding(ReadOnlySpan<byte> headers) => IndexOfAsciiIgnoreCase(headers, "chunked"u8) >= 0;

    /// <summary>在请求头里找 <c>Content-Length</c> 并解析十进制值：没有这个头（GET/HEAD）返回 0。</summary>
    private static int ParseContentLength(ReadOnlySpan<byte> headers)
    {
        var index = IndexOfAsciiIgnoreCase(headers, "content-length:"u8);
        if (index < 0)
        {
            return 0;
        }

        index += "content-length:".Length;
        while (index < headers.Length && headers[index] is (byte)' ' or (byte)'\t')
        {
            index++;
        }

        var value = 0;
        var digits = 0;
        while (index < headers.Length && headers[index] is >= (byte)'0' and <= (byte)'9')
        {
            value = (value * 10) + (headers[index] - '0');
            index++;
            digits++;
        }

        return digits > 0 ? value : 0;
    }

    /// <summary>在字节序列里按 ASCII 大小写不敏感查找子串，返回起始下标；找不到返回 -1。</summary>
    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> value)
    {
        for (var i = 0; i + value.Length <= span.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < value.Length; j++)
            {
                if (Fold(span[i + j]) != value[j])
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

    /// <summary>ASCII 大写字母转小写，其余原样，用于头名与头值比较。</summary>
    private static byte Fold(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

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
