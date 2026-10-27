
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 流式与上传的超时/取消：同步与异步读、限速等待打断、阻塞读打断。
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class StreamCancellationTests
{
    [Fact]
    public async Task PerRequestZeroTimeout_DisablesInstanceDefaultTimeout()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(400);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { Timeout = TimeSpan.FromMilliseconds(100) });

        // 显式 Timeout=0 必须能覆盖实例默认值。
        var response = await client.GetAsync(server.Url + "slow", c => c.Timeout = TimeSpan.Zero);

        Assert.Equal(200, response.Status);
    }

    // ---------------------------------------------------------------
    // P1：流式响应的超时与租约
    // ---------------------------------------------------------------

    [Fact]
    public async Task StreamTimeout_AppliesToBodyRead()
    {
        await using var server = StartSlowBodyServer(SlowBodyDelay);

        using var client = new AxiosClient();
        await client.WarmUpAsync(server.Url + "warmup");

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = BodyReadTimeout);

        await using var stream = response.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => stream.CopyToAsync(Stream.Null));
        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    /// <summary>
    /// 同步 Read 也必须能在等待中被超时打断：令牌交给底层那一次 I/O，
    /// 而不是发起前预检一次（否则读卡在 socket 上时超时形同虚设）。
    /// </summary>
    [Fact]
    public async Task StreamTimeout_InterruptsBlockedSyncRead()
    {
        await using var server = StartSlowBodyServer(SlowBodyDelay);

        using var client = new AxiosClient();
        await client.WarmUpAsync(server.Url + "warmup");

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = BodyReadTimeout);

        await using var stream = response.Data!;

        // 放到后台线程阻塞，避免 xunit 的同步上下文被同步读占住
        var ex = await Task.Run(() => Assert.Throws<AxiosException>(
            () => stream.Read(new byte[16], 0, 16)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    /// <summary>
    /// 同步 Read 上调用方自己传的令牌语义不变：外部取消 → <c>ERR_CANCELED</c>。
    /// 正文延迟取 <see cref="SlowBodyDelay"/>、取消由独立线程触发，避免两个计时器赛跑。
    /// </summary>
    [Fact]
    public async Task StreamExternalCancel_InterruptsBlockedSyncRead()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"ok\":true}"),
            BodyDelay: SlowBodyDelay));

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var response = await client.GetAsync<Stream>(
            server.Url + "slow-body",
            c => c.Timeout = TimeSpan.FromSeconds(30),
            cts.Token);

        await using var stream = response.Data!;

        var canceller = new Thread(() =>
        {
            Thread.Sleep(150);
            cts.Cancel();
        })
        {
            IsBackground = true
        };
        canceller.Start();

        try
        {
            var ex = await Task.Run(() => Assert.Throws<AxiosException>(
                () => stream.Read(new byte[16], 0, 16)));

            Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        }
        finally
        {
            // 取消线程必须收尾：否则 cts 可能在取消进行中被释放
            canceller.Join();
        }
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
        Assert.True(clock.Elapsed < HungReadBudget, $"同步读超时没有及时生效，耗时 {clock.Elapsed}");
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
        Assert.True(clock.Elapsed < HungReadBudget, $"异步读超时没有及时生效，耗时 {clock.Elapsed}");
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
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[4096], ContentType: "application/octet-stream", BodyDelay: SlowBodyDelay));

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
                BodyDelay: SlowBodyDelay));

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
            // 1KB/s：读完 64KB 需要 64 秒，必须被 250ms 的超时打断。
            // 速率压得足够低，才能把「被打断」和「限速自己等完」清楚地区分开（后者远大于 BlockingReadBudget）。
            c.Limits.MaxRate = 1024;
        });
        await using var stream = response.Data!;

        var buffer = new byte[16 * 1024];
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<AxiosException>(() => stream.Read(buffer, 0, buffer.Length));
        clock.Stop();

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.True(clock.Elapsed < BlockingReadBudget, $"限速等待没有响应超时，耗时 {clock.Elapsed}");
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
        Assert.True(clock.Elapsed < HungReadBudget, $"正文读取阶段没有响应超时，耗时 {clock.Elapsed}");
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
        Assert.True(clock.Elapsed < HungReadBudget, $"正文读取阶段没有响应取消，耗时 {clock.Elapsed}");
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
            // 256KB @ 8KB/s ≈ 32 秒才能传完，必须被 250ms 的超时打断。
            // 速率压得足够低，才能把「被打断」与「限速自己等完」清楚地区分开（后者远大于 Budget）。
            c.Limits.MaxRate = 8 * 1024;
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
        var upload = client.PostAsync(server.Url + "upload", payload, c => c.Limits.MaxRate = 64 * 1024, cts.Token);

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
        var firstEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 16 块 × 50ms ≈ 800ms：远长于取消点，保证取消落在上传中途
        var content = new SyncWritingContent(new byte[128 * 1024], chunkSize: 8 * 1024, chunkDelay: TimeSpan.FromMilliseconds(50));

        // 取消点必须是「正文确实开始写了」，而不是固定 150ms：CI 上建连 + 序列化启动可能超过 150ms，
        // 那样取消落在正文之前，进度回调一次都不会触发 —— 断言失败于机器调度，而不是同步桥的实现。
        var upload = client.PostAsync(server.Url + "upload", content, c => c.Callbacks.OnUploadProgress = e =>
        {
            lock (events)
            {
                events.Add(e);
            }

            firstEvent.TrySetResult();
        }, cts.Token);

        await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => upload);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        lock (events)
        {
            Assert.NotEmpty(events);
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

    /// <summary>
    /// 「先发响应头、再延迟正文」的测试服务器，并为预热留一个立即返回的 <c>/warmup</c>。
    /// </summary>
    private static TestHttpServer StartSlowBodyServer(TimeSpan bodyDelay)
        => TestHttpServer.Start(request => request.Path == "/warmup"
            ? TestJson.Response(new { ok = true })
            : new TestResponse(
                Body: Encoding.UTF8.GetBytes("{\"ok\":true}"),
                BodyDelay: bodyDelay));


    /// <summary>
    /// 「必须在预算内被打断」类用例的预算：自然完成时间取 20 秒以上、预算取 5 秒，
    /// 两者差一个数量级才能区分「被打断」与「自己等完」。
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 同步入口阻塞线程池线程时的预算：远小于「限速自己等完」的时间（64KB @1KB/s ≈ 64s），
    /// 又留够线程池注入与计时器回调的延迟。
    /// </summary>
    private static readonly TimeSpan BlockingReadBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 「内层流永不返回」的用例专用预算：只需证明超时 / 取消确实生效，不必精确到几百毫秒。
    /// </summary>
    private static readonly TimeSpan HungReadBudget = TimeSpan.FromSeconds(10);

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
}