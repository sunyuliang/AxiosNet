using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-23 第二轮全局审查补充的回归用例：
/// <list type="number">
/// <item>STJ 二进制入口忽略响应 charset / ResponseEncoding，与接口默认语义不一致；</item>
/// <item>SSE 分帧只认 LF，合法的裸 CR（WHATWG 允许 CR / LF / CRLF）整段被当成一行。</item>
/// </list>
/// </summary>
public sealed class Audit20261023Round2Tests
{
    // ------------------------------------------------------------------
    // Bug 1：非文本 Content-Type + 声明 charset
    // ------------------------------------------------------------------

    [Fact]
    public async Task TypedJson_BinaryResponseWithDeclaredCharset_DecodesWithThatCharset()
    {
        // UTF-16LE、无 BOM：字节入口只有真正按 charset 解码才可能读对。
        var body = Encoding.Unicode.GetBytes("{\"name\":\"测试\"}");
        using var client = CreateClient(
            body,
            contentType: "application/octet-stream; charset=utf-16");

        var response = await client.GetAsync<Payload>("payload");

        Assert.Equal("测试", response.Data!.Name);
    }

    [Fact]
    public async Task TypedJson_BinaryResponseWithForcedResponseEncoding_UsesForcedEncoding()
    {
        var body = Encoding.Unicode.GetBytes("{\"name\":\"强编码\"}");
        using var client = CreateClient(
            body,
            contentType: "application/octet-stream");

        var response = await client.GetAsync<Payload>(
            "payload",
            config => config.Serialization.ResponseEncoding = Encoding.Unicode);

        Assert.Equal("强编码", response.Data!.Name);
    }

    // ------------------------------------------------------------------
    // Bug 2：SSE 裸 CR 行尾
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_BareCarriageReturnLineEndings_AreFramed()
    {
        var body = "data: {\"n\":1}\r\rdata: {\"n\":2}\r\r";
        using var client = CreateStreamingClient(body);

        var items = await CollectAsync(client);

        Assert.Equal(new[] { 1, 2 }, items.Select(i => i.N));
    }

    [Fact]
    public async Task Sse_CrLfSplitAcrossReads_KeepsMultiLineFrameTogether()
    {
        // CR 与 LF 落到两次 ReadAsync：若把边界上的 CR 直接当行尾，
        // LF 会变成空行，多行 data 会被提前交付成非法 JSON。
        var bytes = Encoding.UTF8.GetBytes("data: {\"n\":1,\r\ndata: \"t\":\"ok\"}\r\n\r\n");
        var split = Array.IndexOf(bytes, (byte)'\r') + 1;

        using var client = CreateChunkedStreamingClient(
            new[] { bytes[..split], bytes[split..] });

        var items = await CollectAsync(client);

        var item = Assert.Single(items);
        Assert.Equal(1, item.N);
        Assert.Equal("ok", item.T);
    }

    // ------------------------------------------------------------------
    // Bug 3：流式分帧未校验强制 ResponseEncoding
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_ForcedNonAsciiCompatibleEncoding_IsRejected()
    {
        // 响应未声明 charset，但调用方强制 UTF-16：分帧按字节找 CR / LF 必然切错，
        // 应在取流时直接报 ERR_BAD_RESPONSE，而不是静默产出乱码帧。
        var body = Encoding.Unicode.GetBytes("data: {\"n\":1}\r\n\r\n");
        using var client = CreateChunkedStreamingClient(new[] { body });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestStreamAsync(
                new StreamRequest(),
                AxiosStreamFormat.ServerSentEvents,
                config => config.Serialization.ResponseEncoding = Encoding.Unicode));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static AxiosClient CreateClient(byte[] body, string contentType)
        => new(
            new AxiosConfig { BaseAddress = "http://audit.test/" },
            handler: new StaticHandler(body, contentType),
            disposeHandler: true);

    private static ApiClient CreateStreamingClient(string body)
        => CreateChunkedStreamingClient(new[] { Encoding.UTF8.GetBytes(body) });

    private static ApiClient CreateChunkedStreamingClient(byte[][] chunks)
        => new(
            new AxiosClient(
                new AxiosConfig { BaseAddress = "http://audit.test/" },
                handler: new ChunkedHandler(chunks),
                disposeHandler: true),
            ownsAxiosClient: true);

    private static async Task<List<Frame>> CollectAsync(ApiClient client)
    {
        var response = await client.RequestStreamAsync(
            new StreamRequest(),
            AxiosStreamFormat.ServerSentEvents);

        var items = new List<Frame>();
        await foreach (var item in response.Data!)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public StaticHandler(byte[] body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }

    private sealed class ChunkedHandler : HttpMessageHandler
    {
        private readonly byte[][] _chunks;

        public ChunkedHandler(byte[][] chunks) => _chunks = chunks;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(new ChunkedReadStream(_chunks));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/event-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }

    /// <summary>每次 Read 只返回一个预置分块，用来把 CRLF 拆到两次读取里。</summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _index;
        private int _offset;

        public ChunkedReadStream(byte[][] chunks) => _chunks = chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            while (_index < _chunks.Length)
            {
                var chunk = _chunks[_index];
                if (_offset < chunk.Length)
                {
                    var count = Math.Min(chunk.Length - _offset, destination.Length);
                    chunk.AsSpan(_offset, count).CopyTo(destination);
                    _offset += count;
                    return count;
                }

                _index++;
                _offset = 0;
            }

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class Payload
    {
        public string? Name { get; set; }
    }

    public sealed record Frame(int N, string? T = null);

    [Get("stream")]
    private sealed class StreamRequest : IApiRequest<Frame>
    {
    }
}
