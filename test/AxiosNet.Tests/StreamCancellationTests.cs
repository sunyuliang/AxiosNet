using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 同步 / 异步 I/O 的取消语义（总超时、外部取消、调用方自己的令牌）：
/// <list type="bullet">
/// <item>总超时 → <c>ECONNABORTED</c>，外部取消 → <c>ERR_CANCELED</c>，调用方在 ReadAsync 上自己传的令牌 → 原样 <see cref="OperationCanceledException"/>（不包装成 AxiosException）；</item>
/// <item>同步入口（Read/Write/Flush）与异步入口（ReadAsync/WriteAsync/FlushAsync）必须给出同一套结论；</item>
/// <item>同步入口绝不对内层流做同步 I/O：内层是 async-only 流（同步成员抛 <see cref="NotSupportedException"/>）时，
/// 同步读也必须照常工作，并且能被令牌打断——否则「已经超时了，同步读还卡在 socket 上」的洞会重新出现。</item>
/// </list>
/// </summary>
public sealed class StreamCancellationTests
{
    /// <summary>期望在 250ms 内被打断；给 2 秒的宽松上限，避免 CI 抖动导致假失败。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>足够大、不参与断言的超时：只用于让 TimeoutStream 把外部令牌接进链路。</summary>
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>
    /// 只实现异步、同步成员一律抛 <see cref="NotSupportedException"/> 的流（async-only），并且异步读写永不完成。
    /// 任何包装层若把同步 I/O 压到内层，测试会立刻看到 NotSupportedException 而不是预期的 AxiosException。
    /// </summary>
    private sealed class AsyncOnlyHangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException("async-only stream 不支持 Length");

        public override long Position
        {
            get => throw new NotSupportedException("async-only stream 不支持 Position");
            set => throw new NotSupportedException("async-only stream 不支持 Position");
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("async-only stream：同步 Read 不可用");

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("async-only stream：同步 Write 不可用");

        public override void Flush()
            => throw new NotSupportedException("async-only stream：同步 Flush 不可用");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadHangAsync(cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ReadHangAsync(cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => HangAsync(cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(HangAsync(cancellationToken));

        public override Task FlushAsync(CancellationToken cancellationToken) => HangAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private static async ValueTask<int> ReadHangAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        private static async Task HangAsync(CancellationToken cancellationToken)
            => await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>离线桩 Handler：直接返回固定内容，超时/取消完全由令牌驱动，不受真实网络影响。</summary>
    private sealed class FixedContentHandler : HttpMessageHandler
    {
        private readonly HttpContent _content;

        public FixedContentHandler(HttpContent content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// 在 <c>SerializeToStreamAsync</c> 里用同步 <c>stream.Write</c> 写请求体：
    /// 普通内容走 <c>CopyToAsync</c> 只会命中 <c>ProgressWriteStream.WriteAsync</c>，
    /// 只有这种自定义内容会命中 <c>ProgressWriteStream.Write</c> 这条同步桥。
    /// </summary>
    private sealed class SyncWritingContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly int _chunkSize;
        private readonly TimeSpan _chunkDelay;

        public SyncWritingContent(byte[] payload, int chunkSize, TimeSpan chunkDelay)
        {
            _payload = payload;
            _chunkSize = chunkSize;
            _chunkDelay = chunkDelay;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < _payload.Length; offset += _chunkSize)
            {
                var count = Math.Min(_chunkSize, _payload.Length - offset);

                // 同步写：阻塞等待 ProgressWriteStream 的带进度/限速异步核心
                stream.Write(_payload, offset, count);

                await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
            }

            stream.Flush();
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private static AxiosClient ClientWith(HttpContent content)
        => new(handler: new FixedContentHandler(content), disposeHandler: false);

    private static async Task<Stream> OpenHangingStreamAsync(AxiosClient client, TimeSpan timeout)
    {
        var response = await client.GetAsync<Stream>("http://probe.invalid/slow", c => c.Timeout = timeout);
        return response.Data!;
    }

    // ------------------------------------------------------------------
    // 流式响应：同步读 / 异步读
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamRead_Sync_HonorsTimeout()
    {
        using var client = ClientWith(new StreamContent(new AsyncOnlyHangingStream()));
        await using var stream = await OpenHangingStreamAsync(client, ShortTimeout);

        var buffer = new byte[512];
        var clock = Stopwatch.StartNew();

        // 内层是 async-only：这里若拿到 NotSupportedException，说明同步成员把同步 I/O 压给了内层流
        var ex = Assert.Throws<AxiosException>(() => stream.Read(buffer, 0, buffer.Length));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"同步读超时没有及时生效，耗时 {clock.Elapsed}");
    }

    [Fact]
    public async Task StreamRead_Async_HonorsTimeout()
    {
        using var client = ClientWith(new StreamContent(new AsyncOnlyHangingStream()));
        await using var stream = await OpenHangingStreamAsync(client, ShortTimeout);

        var buffer = new byte[512];
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.ReadAsync(buffer, 0, buffer.Length));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"异步读超时没有及时生效，耗时 {clock.Elapsed}");
    }

    /// <summary>
    /// 外部取消必须能打断**正在阻塞**的同步读：令牌交给的是底层那一次 I/O，
    /// 而不是发起前预检一次（否则同步读会一直卡到数据到达）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamRead_HonorsExternalCancellation(bool asyncRead)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[4096],
            ContentType: "application/octet-stream",
            BodyDelay: TimeSpan.FromSeconds(3)));

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var response = await client.GetAsync<Stream>(
            server.Url + "slow.bin",
            c => c.Timeout = LongTimeout,
            cts.Token);
        await using var stream = response.Data!;

        var buffer = new byte[512];
        var clock = Stopwatch.StartNew();
        var canceller = Task.Run(async () =>
        {
            await Task.Delay(150);
            cts.Cancel();
        });

        try
        {
            if (asyncRead)
            {
                var asyncEx = await Assert.ThrowsAsync<AxiosException>(() => stream.ReadAsync(buffer, 0, buffer.Length));
                Assert.Equal(AxiosException.ErrCanceled, asyncEx.Code);
            }
            else
            {
                var syncEx = Assert.Throws<AxiosException>(() => stream.Read(buffer, 0, buffer.Length));
                Assert.Equal(AxiosException.ErrCanceled, syncEx.Code);
            }
        }
        finally
        {
            await canceller;
            clock.Stop();
        }

        Assert.True(clock.Elapsed < Budget, $"外部取消没有打断正文读取，耗时 {clock.Elapsed}");
    }

    /// <summary>
    /// 调用方在 ReadAsync 上自己传的令牌取消时，异常原样抛出：
    /// 既不翻译成 ECONNABORTED，也不包装成 AxiosException（那是「请求被取消」的语义，这里是「这次读被取消」）。
    /// </summary>
    [Fact]
    public async Task StreamReadAsync_WithCallerToken_ThrowsRawOperationCanceled()
    {
        using var client = ClientWith(new StreamContent(new AsyncOnlyHangingStream()));
        await using var stream = await OpenHangingStreamAsync(client, LongTimeout);

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var buffer = new byte[16];

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(buffer, 0, buffer.Length, callerCts.Token));

        Assert.IsNotType<AxiosException>(ex);
        Assert.True(callerCts.IsCancellationRequested);
    }

    /// <summary>
    /// 真实连接上的整车链路（LeaseHoldingStream → TimeoutStream → 原始连接流）：
    /// 服务端先发响应头、后发正文，随后阻塞在同步 Read 上，超时必须能在正文阶段把它打断。
    /// 响应流本身只读（HttpContent 侧是 ReadOnlyStream），所以这一层只测读，写路径由上传用例覆盖。
    /// </summary>
    [Fact]
    public async Task StreamRead_Sync_HonorsTimeout_DuringBodyRead()
    {
        await using var server = TestHttpServer.Start(request => request.Path == "/warmup"
            ? TestJson.Response(new { ok = true })
            : new TestResponse(
                Body: new byte[4096],
                ContentType: "application/octet-stream",
                BodyDelay: TimeSpan.FromSeconds(3)));

        using var client = new AxiosClient();

        // 先预热同源连接：ShortTimeout 是整条请求的预算，不能被建连/建 Handler 吃掉
        await client.WarmUpAsync(server.Url + "warmup");

        var response = await client.GetAsync<Stream>(
            server.Url + "slow.bin",
            c => c.Timeout = ShortTimeout);
        await using var stream = response.Data!;

        var buffer = new byte[512];
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<AxiosException>(() => stream.Read(buffer, 0, buffer.Length));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"同步读没有响应正文阶段的超时，耗时 {clock.Elapsed}");
    }

    // ------------------------------------------------------------------
    // 限速包装：同步读必须走 Task.Delay 的异步等待，且能被超时打断
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamRead_Sync_TimeoutInterruptsRateLimitWait()
    {
        var payload = new byte[64 * 1024];
        using var client = ClientWith(new StreamContent(new MemoryStream(payload)));

        var response = await client.GetAsync<Stream>("http://probe.invalid/file", c =>
        {
            c.Timeout = ShortTimeout;
            c.MaxRate = 8 * 1024; // 8KB/s：读完 64KB 需要约 8 秒，必须被 250ms 的超时打断
        });
        await using var stream = response.Data!;

        var buffer = new byte[16 * 1024];
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<AxiosException>(() => stream.Read(buffer, 0, buffer.Length));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"限速等待没有响应超时，耗时 {clock.Elapsed}");
    }

    // ------------------------------------------------------------------
    // 非流式响应：库内读完正文的阶段也要被超时/取消覆盖
    // ------------------------------------------------------------------

    [Fact]
    public async Task BufferedResponse_HonorsTimeoutDuringBodyRead()
    {
        using var client = ClientWith(new StreamContent(new AsyncOnlyHangingStream()));

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>("http://probe.invalid/slow", c => c.Timeout = ShortTimeout));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"正文读取阶段没有响应超时，耗时 {clock.Elapsed}");
    }

    [Fact]
    public async Task BufferedResponse_HonorsExternalCancellationDuringBodyRead()
    {
        using var client = ClientWith(new StreamContent(new AsyncOnlyHangingStream()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>("http://probe.invalid/slow", cancellationToken: cts.Token));
        clock.Stop();

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"正文读取阶段没有响应取消，耗时 {clock.Elapsed}");
    }

    // ------------------------------------------------------------------
    // 上传：异步写（CopyToAsync）与同步写（自定义内容里的 stream.Write）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Upload_TimeoutInterruptsRateLimitWait()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var payload = new byte[256 * 1024];

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "upload", payload, c =>
        {
            c.MaxRate = 64 * 1024; // 4 秒才能传完，必须被 250ms 的超时打断
            c.Timeout = ShortTimeout;
        }));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < Budget, $"上传限速等待没有响应超时，耗时 {clock.Elapsed}");
    }

    [Fact]
    public async Task Upload_HonorsExternalCancellation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var payload = new byte[256 * 1024];
        var upload = client.PostAsync(server.Url + "upload", payload, c => c.MaxRate = 64 * 1024, cts.Token);

        await Task.Delay(150);
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => upload);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    /// <summary>
    /// 自定义内容里的同步 <c>stream.Write</c> 命中 ProgressWriteStream 的同步桥：
    /// 进度回调必须照常上报（证明走的确实是包装后的流），取消也必须照常翻译成 ERR_CANCELED。
    /// </summary>
    [Fact]
    public async Task Upload_SyncWriteFromCustomContent_HonorsCancellation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var events = new List<AxiosProgressEvent>();
        // 16 块 × 50ms ≈ 800ms：远长于 150ms 的取消点，保证取消一定落在上传中途
        var content = new SyncWritingContent(new byte[128 * 1024], chunkSize: 8 * 1024, chunkDelay: TimeSpan.FromMilliseconds(50));

        var upload = client.PostAsync(server.Url + "upload", content, c => c.OnUploadProgress = events.Add, cts.Token);

        await Task.Delay(150);
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => upload);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.NotEmpty(events);
    }
}
