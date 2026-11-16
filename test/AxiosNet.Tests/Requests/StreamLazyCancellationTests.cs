using System.Net;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 流式响应的取消能力<b>按需建立</b>：既不限时、外部令牌也不可取消时，交给底层流的令牌应当是
/// <see cref="CancellationToken.None"/>；反过来，只要有任一取消来源，令牌就必须仍然可取消。
/// </summary>
/// <remarks>
/// 结构断言（令牌身份 / 可取消性）而不是分配测量：分配会随运行时与线程局部分配计数漂移，
/// 而「有没有建令牌源」直接体现在交给底层的那一次 I/O 收到的令牌上。
/// 取消 / 超时 / 预算耗尽的行为回归由 <c>StreamCancellationTests</c> 与
/// <c>TimeoutAndCancellationTests</c> 覆盖，这里只锁住懒建的边界。
/// </remarks>
public sealed class StreamLazyCancellationTests
{
    /// <summary>无超时预算 + 不可取消令牌：不建任何 CTS，底层拿到的就是 <see cref="CancellationToken.None"/>。</summary>
    [Fact]
    public async Task NoTimeoutNoExternalToken_HandsNonCancelableToken()
    {
        var content = new RecordingContent(1024);
        using var client = new AxiosClient(handler: new RecordingHandler(content));

        var response = await client.GetAsync<Stream>("http://probe.invalid/big");
        await using var stream = response.Data!;

        var total = await ReadAllAsync(stream);

        Assert.Equal(1024, total);
        Assert.NotEmpty(content.Tokens);
        Assert.All(content.Tokens, token => Assert.False(
            token.CanBeCanceled,
            "不限时且外部令牌不可取消时不该创建 CancellationTokenSource：交给底层的令牌应不可取消。"));
    }

    /// <summary>有超时（外部令牌不可取消）：只需要计时器，令牌仍必须可取消（否则超时会被架掉）。</summary>
    [Fact]
    public async Task TimeoutWithoutExternalToken_StillHandsCancelableToken()
    {
        var content = new RecordingContent(1024);
        using var client = new AxiosClient(handler: new RecordingHandler(content));

        var response = await client.GetAsync<Stream>("http://probe.invalid/big", c => c.Timeout = TimeSpan.FromSeconds(30));
        await using var stream = response.Data!;

        var total = await ReadAllAsync(stream);

        Assert.Equal(1024, total);
        Assert.NotEmpty(content.Tokens);
        Assert.All(content.Tokens, token => Assert.True(
            token.CanBeCanceled,
            "设了 Timeout 就必须把计时令牌交给底层那一次 I/O，否则读卡在 socket 上时超时形同虚设。"));
    }

    /// <summary>无超时但外部令牌可取消：令牌同样必须可取消（外部取消要在正文阶段生效）。</summary>
    [Fact]
    public async Task ExternalTokenWithoutTimeout_StillHandsCancelableToken()
    {
        var content = new RecordingContent(1024);
        using var client = new AxiosClient(handler: new RecordingHandler(content));
        using var cts = new CancellationTokenSource();

        var response = await client.GetAsync<Stream>(
            "http://probe.invalid/big",
            configure: null,
            cancellationToken: cts.Token);
        await using var stream = response.Data!;

        var total = await ReadAllAsync(stream);

        Assert.Equal(1024, total);
        Assert.NotEmpty(content.Tokens);
        Assert.All(content.Tokens, token => Assert.True(
            token.CanBeCanceled,
            "调用方给了可取消令牌时，正文读取必须能被它打断。"));
    }

    /// <summary>读完整个正文并返回字节数（同步推进，避免测量不到真正的读路径）。</summary>
    private static async Task<int> ReadAllAsync(Stream stream)
    {
        var buffer = new byte[256];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return total;
            }

            total += read;
        }
    }

    /// <summary>固定内容的桩 Handler：正文来自 <see cref="RecordingContent"/> 自带的流。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly RecordingContent _content;

        public RecordingHandler(RecordingContent content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = _content;
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>自带记录流的 <see cref="HttpContent"/>：把库每次交给底层的令牌留下来。</summary>
    private sealed class RecordingContent : HttpContent
    {
        private readonly RecordingStream _stream;

        public RecordingContent(int size) => _stream = new RecordingStream(size);

        public IReadOnlyList<CancellationToken> Tokens => _stream.Tokens;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        /// <summary>只读、一次性的内存流：记录每次读拿到的令牌，返回固定长度的零字节。</summary>
        private sealed class RecordingStream : Stream
        {
            private readonly byte[] _payload;
            private int _position;

            public RecordingStream(int size) => _payload = new byte[size];

            public List<CancellationToken> Tokens { get; } = new();

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
                => ReadInto(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Tokens.Add(cancellationToken);
                return new ValueTask<int>(ReadInto(buffer.Span));
            }

            // 私有方法不能 override 虚成员：与 Stream.Read(Span<byte>) 同签名会触发 CS0114，改名消除。
            private int ReadInto(Span<byte> buffer)
            {
                var take = Math.Min(buffer.Length, _payload.Length - _position);
                _payload.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
        }
    }
}
