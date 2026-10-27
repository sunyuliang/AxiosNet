
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Net;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应正文读取：正文上限与截断映射（第一段）、缓冲策略（第二段）。
/// <para>
/// 两段合在一个文件里，因为它们是同一个被测动作的两面：<c>Content-Length</c> 只是提示，
/// 既决定「读到哪里算完」（缓冲），也决定「超过上限怎么报」（错误映射）。
/// </para>
/// <para>
/// 分帧缓冲的存留（超大元素 / 超大帧收尾后不得按峰值长驻）不在本文件——那是声明式流式的内部实现，
/// 归 <c>WebAPI/StreamingTests</c>；分配护栏归 <c>Regression/AllocationTests</c>。
/// </para>
/// </summary>
public sealed class ResponseBodyTests
{
    // ===============================================================
    // 响应正文读取阶段的 HttpRequestException 也必须归一到 ERR_NETWORK
    // （类注释承诺「统一正文阶段的取消 / IO 异常」，实际只 catch 了 IOException）。
    // ===============================================================

    [Fact]
    public async Task ResponseBodyReadFailure_MapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new ThrowingReadHandler(), disposeHandler: false);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://localhost/data"));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    [Theory]
    [InlineData(4)]    // 恰好等于上限：必须放行（判定是 ≤，不是 <）
    [InlineData(1024)] // 上限远大于正文
    public async Task MaxContentLength_WithinOrAtLimit_ReturnsBody(int limit)
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<byte[]>(server.Url + "small", c => c.Limits.MaxContentLength = limit);

        Assert.Equal(payload, response.Data);
    }

    [Theory]
    [InlineData(4, 3)]      // 边界：只超 1 字节也要拒（判定是 >，不是 >=）
    [InlineData(4096, 1024)]
    public async Task MaxContentLength_DeclaredLengthExceedsLimit_RejectsWithLimitError(int size, int limit)
    {
        var payload = new byte[size];
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: payload, ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<byte[]>(server.Url + "big", c => c.Limits.MaxContentLength = limit));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Contains("maxContentLength", ex.Message);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task MaxContentLength_DeclaredLengthExceedsLimit_StreamResponseRejectsBeforeReturning()
    {
        // responseType=Stream 的判据不同：拒绝必须发生在「把响应交回调用方」之前。
        // 只靠读时计数是过不了这条的——那样调用方已经拿到流，上限要等它自己去读才炸（此前已当成功）。
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: new byte[8192], ContentType: "application/octet-stream"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<Stream>(server.Url + "big", c => c.Limits.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Contains("maxContentLength", ex.Message);
    }

    // 流式响应也执行 maxContentLength（含未声明长度的 chunked）

    [Fact]
    public async Task StreamingResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[64 * 1024], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "chunked", c => c.Limits.MaxContentLength = 1024);
        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task BufferedResponse_WithoutDeclaredLength_EnforcesMaxContentLength()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[64 * 1024], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(server.Url + "chunked", c => c.Limits.MaxContentLength = 1024));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // 流式正文：逐字节读取仍受长度上限约束
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ReadByte_EnforcesMaxContentLength()
    {
        // chunked：服务端不声明长度，上限只能靠「边读边计数」生效
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[64], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "chunked", c => c.Limits.MaxContentLength = 4);
        await using var stream = response.Data!;

        // ReadByte 不覆写，靠基类换算成 Read(byte[],int,int)：必须落到同一条计数路径上
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, stream.ReadByte());
        }

        var ex = Assert.Throws<AxiosException>(() => { stream.ReadByte(); });
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // 截断正文：声明长度没读满就断流，必须归一到 ERR_NETWORK
    // ---------------------------------------------------------------

    [Fact]
    public async Task TruncatedResponseBody_IsWrappedAsNetworkError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"partial\":1"),
            ContentType: "application/json",
            CloseConnection: true,
            ContentLengthOverride: 4096));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "truncated"));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.NotNull(ex.Cause);
        Assert.IsAssignableFrom<IOException>(ex.Cause);
    }

    // ===============================================================
    // 缓冲策略：声明长度只是提示、跨段装配
    // ===============================================================

    // 1. 声明长度偏短：不得截断
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_IsNotTruncated()
    {
        var payload = Payload(200_000, seed: 3);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: 1024));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        // 缺陷版本只交回 1024 字节：读到声明值就 return，后面的数据被静默丢弃。
        Assert.Equal(payload.Length, response.Data!.Length);
        Assert.Equal(payload, response.Data);
        Assert.Equal(payload.Length, response.BodyBytes);
    }

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_StillEnforcesMaxContentLength()
    {
        var payload = Payload(200_000, seed: 4);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: 1024));

        // 上限判的是真实字节数：声明 1 KB 不等于正文 1 KB。
        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(
            BlobUrl,
            c =>
            {
                c.ResponseType = ResponseType.ArrayBuffer;
                c.Limits.MaxContentLength = 4096;
            }));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("maxContentLength", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_TextResponseIsComplete()
    {
        var text = new string('a', 100_000);
        var payload = Encoding.UTF8.GetBytes(text);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: payload.Length / 2));

        var response = await client.GetAsync<string>(BlobUrl, c => c.ResponseType = ResponseType.Text);

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task DeclaredLength_SatisfiedThenStreamBreaks_StillSucceeds()
    {
        // 正文已按声明读满，随后连接就断了（尾部探测撞上断流）：请求本身不该失败。
        var payload = Payload(64 * 1024, seed: 7);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: payload.Length, breakAfterBody: true));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, response.Data);
    }

    // ------------------------------------------------------------------
    // 2. 段式累积：跨多个池化段装配后字节一致
    // ------------------------------------------------------------------

    [Fact]
    public async Task UndeclaredLength_MultiSegment_AssemblesExactBytes()
    {
        // 300 KB、每块 16 KB、不声明长度：必然跨 81920 / 163840 / … 多个池化段。
        var payload = Payload(300 * 1024, seed: 6);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: null, chunk: 16 * 1024));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload.Length, response.Data!.Length);
        Assert.Equal(payload, response.Data);
    }

    // ===============================================================
    // 测试夹具
    // ===============================================================

    /// <summary>正文读取时抛 <see cref="HttpRequestException"/> 的响应体（模拟连接中途失效）。</summary>
    private sealed class ThrowingReadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowingReadContent()
            });
    }

    private sealed class ThrowingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ThrowingReadStream());
    }

    private sealed class ThrowingReadStream : Stream
    {
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

        public override int Read(byte[] buffer, int offset, int count)
            => throw new HttpRequestException("读取响应正文时连接失效（模拟）");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpRequestException("读取响应正文时连接失效（模拟）"));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 分块写出、块间带 60ms 间隔的正文：让「传满声明长度即锁死」的判定在重发轮次里
    /// 必然产生一次非 force 的上报（而不是靠最终 Complete 的强制上报掩盖问题）。
    /// </summary>
    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly int _chunks;
        private readonly int _chunkSize;

        public SlowChunkedContent(int chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            Headers.ContentLength = (long)chunks * chunkSize;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[_chunkSize];
            for (var i = 0; i < _chunks; i++)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)_chunks * _chunkSize;
            return true;
        }
    }


    /// <summary>Dispose 时阻塞到手动放行的 Handler，用于验证「释放不在锁内执行」。</summary>
    private sealed class BlockingDisposeHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }


    /// <summary>记录 Handler 创建/释放次数：用于断言驱逐与关闭「不漏释放、不重复释放」。</summary>
    private sealed class HandlerLedger
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public HttpMessageHandler Create()
        {
            Interlocked.Increment(ref _created);
            return new LedgerHandler(this);
        }

        private sealed class LedgerHandler : HttpMessageHandler
        {
            private readonly HandlerLedger _owner;
            private int _disposed;

            public LedgerHandler(HandlerLedger owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                base.Dispose(disposing);
            }
        }
    }

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。</para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);


    private const string BlobUrl = "http://fixed.local/blob";


    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    /// <summary>按块返回正文的 Handler：<paramref name="declaredLength"/> 为 <c>null</c> 时不声明长度（等价 chunked）。</summary>
    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long? _declared;
        private readonly int _chunk;
        private readonly bool _breakAfterBody;

        public BodyHandler(byte[] body, long? declaredLength, int chunk = 8 * 1024, bool breakAfterBody = false)
        {
            _body = body;
            _declared = declaredLength;
            _chunk = chunk;
            _breakAfterBody = breakAfterBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ChunkedContent(_body, _chunk, _declared, _breakAfterBody);
            if (_declared is long declared)
            {
                content.Headers.ContentLength = declared;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>每次 <c>Read</c> 最多给 <c>chunk</c> 字节的响应内容；声明长度与真实正文可以不一致。</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _body;
        private readonly int _chunk;
        private readonly long? _declared;
        private readonly bool _breakAfterBody;

        public ChunkedContent(byte[] body, int chunk, long? declared, bool breakAfterBody)
        {
            _body = body;
            _chunk = chunk;
            _declared = declared;
            _breakAfterBody = breakAfterBody;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = _declared ?? 0;
            return _declared is not null;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ChunkStream(_body, _chunk, _breakAfterBody));

        private sealed class ChunkStream : Stream
        {
            private readonly byte[] _body;
            private readonly int _chunk;
            private readonly bool _breakAfterBody;
            private int _position;

            public ChunkStream(byte[] body, int chunk, bool breakAfterBody)
            {
                _body = body;
                _chunk = chunk;
                _breakAfterBody = breakAfterBody;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => new(Fill(buffer.Span));

            private int Fill(Span<byte> buffer)
            {
                if (_position >= _body.Length)
                {
                    if (_breakAfterBody)
                    {
                        throw new IOException("连接在正文之后被重置");
                    }

                    return 0;
                }

                var take = Math.Min(Math.Min(buffer.Length, _chunk), _body.Length - _position);
                _body.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
        }
    }
}
