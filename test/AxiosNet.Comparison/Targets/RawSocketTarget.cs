using System.Net;
using System.Net.Sockets;
using System.Text;
using AxiosNet.Comparison.Models;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 服务端基线：用裸 TCP + 预编码请求字节跑同一个端点，不经过任何 HTTP 客户端栈。
/// <para>
/// 它的作用有两个：一是给出「这台回环服务器本身能跑多快」（其它实现的天花板），
/// 二是测量「服务端每请求倾斜多少分配」——分配是进程级的，客户端实现的净分配必须减掉这份固定开销才可比。
/// </para>
/// <para>
/// 不做 HTTP 解析，只按响应头里的 <c>Content-Length</c> 把字节读满；没有流水线，一次一请求。
/// </para>
/// </summary>
internal sealed class RawSocketTarget : ITarget
{
    private readonly IPEndPoint _endpoint;
    private readonly byte[] _request;

    public RawSocketTarget(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        _endpoint = new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
        _request = Encoding.ASCII.GetBytes(
            $"GET /{Payloads.Route} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: keep-alive\r\n\r\n");
    }

    public string Name => "RawSocket";

    public ITargetSession OpenSession() => new Session(_endpoint, _request);

    public void Dispose()
    {
    }

    private sealed class Session : ITargetSession
    {
        private const int BufferSize = 16 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly byte[] _request;
        private readonly byte[] _buffer = new byte[BufferSize];
        private int _filled;

        public Session(IPEndPoint endpoint, byte[] request)
        {
            _client = new TcpClient { NoDelay = true };
            _client.Connect(endpoint);
            _stream = _client.GetStream();
            _request = request;
        }

        public async Task SendAsync(CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(_request, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                if (TryGetResponseLength(_buffer.AsSpan(0, _filled), out var total)
                    && _filled >= total)
                {
                    var leftover = _filled - total;
                    if (leftover > 0)
                    {
                        Buffer.BlockCopy(_buffer, total, _buffer, 0, leftover);
                    }

                    _filled = leftover;
                    return;
                }

                if (_filled == _buffer.Length)
                {
                    throw new IOException($"响应超过 {BufferSize} 字节缓冲区。");
                }

                var read = await _stream.ReadAsync(_buffer.AsMemory(_filled), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("服务器在响应结束前关闭了连接。");
                }

                _filled += read;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>从响应头算出整条响应长度：头尾 + <c>Content-Length</c>。头不完整或没有长度头时返回 false。</summary>
    private static bool TryGetResponseLength(ReadOnlySpan<byte> span, out int total)
    {
        total = 0;

        var headerEnd = -1;
        for (var i = 0; i + 3 < span.Length; i++)
        {
            if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n'
                && span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
            {
                headerEnd = i;
                break;
            }
        }

        if (headerEnd < 0 || !TryParseContentLength(span[..headerEnd], out var contentLength))
        {
            return false;
        }

        total = headerEnd + 4 + contentLength;
        return true;
    }

    private static bool TryParseContentLength(ReadOnlySpan<byte> headers, out int value)
    {
        value = 0;

        ReadOnlySpan<byte> name = "content-length:"u8;
        for (var i = 0; i + name.Length <= headers.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < name.Length; j++)
            {
                if (Fold(headers[i + j]) != name[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            var index = i + name.Length;
            while (index < headers.Length && headers[index] == (byte)' ')
            {
                index++;
            }

            var digits = 0;
            while (index < headers.Length && headers[index] is >= (byte)'0' and <= (byte)'9')
            {
                value = (value * 10) + (headers[index] - '0');
                index++;
                digits++;
            }

            return digits > 0;
        }

        return false;
    }

    private static byte Fold(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
