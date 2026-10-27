
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Governance;

/// <summary>
/// 进度与限速：上传下载进度、字节限速、重定向/重试下的累计口径。
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class ProgressAndRateTests
{
    // ------------------------------------------------------------------
    // 上传进度：307 重定向会重发同一份正文，进度必须单调推进而不是掉回 0
    // ------------------------------------------------------------------

    [Fact]
    public async Task UploadProgress_AccumulatesAcrossRedirect()
    {
        await using var server = TestHttpServer.Start(request => request.Path == "/redirect"
            ? new TestResponse(
                307,
                "Temporary Redirect",
                Headers: new Dictionary<string, string> { ["Location"] = "/final" })
            : new TestResponse());

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var payload = new byte[256 * 1024];
        var loaded = new List<long>();
        var progresses = new List<double?>();

        await client.PostAsync("redirect", payload, c =>
        {
            c.ContentType = AxiosContentType.OctetStream;
            c.Callbacks.OnUploadProgress = e =>
            {
                loaded.Add(e.Loaded);
                progresses.Add(e.Progress);
            };
        });

        Assert.Equal(2, server.Received.Count);

        Assert.NotEmpty(loaded);

        // 单调不减：重发不应让计量器从 0 重新报。
        for (var i = 1; i < loaded.Count; i++)
        {
            Assert.True(loaded[i] >= loaded[i - 1], $"进度回跳：{loaded[i - 1]} → {loaded[i]}");
        }

        // 跨次累计：两次发送的总字节都要计入。
        Assert.True(loaded[^1] > payload.Length, $"最后一次上报 {loaded[^1]} 未跨次累计");

        // 百分比封顶 100%：累计超过 total 时不能报出 >1 的进度
        Assert.All(progresses, p => Assert.True(p is null or <= 1d));
    }

    // ------------------------------------------------------------------
    // 3) 进度回调的错误模型
    // ------------------------------------------------------------------

    [Fact]
    public async Task DownloadProgressCallbackThrows_ReportsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = Encoding.UTF8.GetBytes(new string('a', 64 * 1024)),
            ContentType = "text/plain"
        });
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Callbacks.OnDownloadProgress = _ => throw new InvalidOperationException("boom")));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("OnDownloadProgress", ex.Message);
    }

    [Fact]
    public async Task UploadProgressCallbackThrows_ReportsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(
                "x",
                new string('a', 200_000),
                c => c.Callbacks.OnUploadProgress = _ => throw new InvalidOperationException("boom")));

        // 回调抛出的异常不应被包成 HttpRequestException 再翻译成 ERR_NETWORK。
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("OnUploadProgress", ex.Message);
    }

    /// <summary>
    /// B：声明长度不可信时，「完成」不能只看 <c>loaded &gt;= total</c>。
    /// <para>
    /// 服务端把 <c>Content-Length</c> 写成 0 却仍有正文（或注入的 Handler 不按声明长度截断）时，
    /// <c>0 &gt;= 0</c> 恒真 —— 缺陷版本在第一次上报后就把计量器锁死，后面几百 KB 全部静默，
    /// 调用方只看到一次「loaded = 第一块 / total = 0」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DownloadProgress_KeepsReporting_WhenDeclaredLengthIsZero()
    {
        var body = new byte[512 * 1024];
        new Random(7).NextBytes(body);

        using var client = new AxiosClient(defaults: new AxiosConfig(), handler: new FixedLengthHandler(body, declaredLength: 0));

        var events = new List<AxiosProgressEvent>();
        var response = await client.GetAsync<byte[]>(
            "http://fixed.local/blob",
            c => c.Callbacks.OnDownloadProgress = events.Add);

        // 正文本身一直是完整的（进度计量不参与数据读取）
        Assert.Equal(body.Length, response.Data!.Length);

        Assert.True(events.Count > 1, $"{events.Count} 次进度上报：{string.Join(", ", events)}");
        Assert.Equal(body.Length, events[^1].Loaded);
        Assert.Equal(0L, events[^1].Total);
    }

    // ===============================================================
    // 3. 307 重发正文：累计进度的终值必须覆盖两轮。
    // ===============================================================

    [Fact]
    public async Task UploadProgress_AfterRedirect_FinalValueCoversBothRounds()
    {
        await using var server = TestHttpServer.Start(request => request.Path == "/redirect"
            ? new TestResponse(
                307,
                "Temporary Redirect",
                Headers: new Dictionary<string, string> { ["Location"] = "/final" })
            : new TestResponse());

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        const int size = 256 * 1024;
        var events = new List<long>();

        // 分块 + 块间间隔 > 50ms：让第二轮也产生「非 force」的上报，
        // 而不是靠 Complete 的强制上报掩盖「传满即锁死」的判定错误。
        await client.PostAsync("redirect", new SlowChunkedContent(chunks: 8, chunkSize: size / 8), c =>
        {
            c.ContentType = AxiosContentType.OctetStream;
            c.Callbacks.OnUploadProgress = e => events.Add(e.Loaded);
        });

        Assert.Equal(2, server.Received.Count);
        Assert.NotEmpty(events);

        // 两轮各写一次正文 → 累计字节数必须是 2×payload
        Assert.Equal(2L * size, events[^1]);
    }

    // ---------------------------------------------------------------
    // 进度事件：服务端未声明长度
    // ---------------------------------------------------------------

    [Fact]
    public async Task DownloadProgress_WithoutDeclaredLength_ReportsNullTotalAndProgress()
    {
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[size], ContentType: "application/octet-stream", Chunked: true));

        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();
        await client.GetAsync<byte[]>(server.Url + "chunked", c => c.Callbacks.OnDownloadProgress = events.Add);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Null(e.Total));
        Assert.All(events, e => Assert.Null(e.Progress));

        // 总量未知时仍要报出准确的已传输字节，且最终事件只报一次满载值
        Assert.Equal(size, events[^1].Loaded);
        Assert.Single(events, e => e.Loaded == size);
    }

    [Fact]
    public async Task NegativeMaxRateAndLimits_ThrowBadOptionValue()
    {
        using var client = new AxiosClient();

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxRate = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxContentLength = -1))).Code);

        Assert.Equal(
            AxiosException.ErrBadOptionValue,
            (await Assert.ThrowsAsync<AxiosException>(() =>
                client.GetAsync("http://127.0.0.1:1/x", c => c.Limits.MaxBodyLength = -1))).Code);
    }

    [Fact]
    public async Task DownloadProgress_ReportsLoadedAndTotal()
    {
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();

        await client.GetAsync<byte[]>(server.Url + "file", c =>
            c.Callbacks.OnDownloadProgress = events.Add);

        Assert.NotEmpty(events);
        var last = events[^1];
        Assert.Equal(size, last.Loaded);
        Assert.Equal(size, last.Total);
        Assert.Equal(1d, last.Progress);
        Assert.False(last.Upload);
        Assert.All(events, e => Assert.False(e.Upload));
        Assert.All(events, e => Assert.True(e.Loaded <= size));
    }

    [Fact]
    public async Task UploadProgress_ReportsLoadedAndTotal()
    {
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();
        var payload = new byte[size];

        await client.PostAsync(server.Url + "upload", payload, c =>
            c.Callbacks.OnUploadProgress = events.Add);

        Assert.NotEmpty(events);
        var last = events[^1];
        Assert.Equal(size, last.Loaded);
        Assert.Equal(size, last.Total);
        Assert.Equal(1d, last.Progress);
        Assert.True(last.Upload);
        Assert.All(events, e => Assert.True(e.Upload));

        // 服务端确实收到了完整请求体
        Assert.Equal(size, server.Received[0].Body.Length);
    }

    [Fact]
    public async Task StreamResponse_ProgressFiresWhileReading()
    {
        const int size = 128 * 1024;
        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();
        var response = await client.GetAsync<Stream>(server.Url + "file", c =>
            c.Callbacks.OnDownloadProgress = events.Add);

        // 返回流时还没开始读，进度可能为 0 条
        await using (var stream = response.Data!)
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.NotEmpty(events);
        Assert.Equal(size, events[^1].Loaded);
        Assert.Equal(size, events[^1].Total);
    }

    [Fact]
    public async Task MaxRate_ThrottlesDownload()
    {
        const int size = 64 * 1024;
        const long rate = 256 * 1024; // 256 KB/s -> 约 250ms

        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync<byte[]>(server.Url + "file", c => c.Limits.MaxRate = rate);
        stopwatch.Stop();

        Assert.Equal(size, response.Data!.Length);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 120,
            $"限速未生效：{stopwatch.ElapsedMilliseconds}ms 就读完了 {size} 字节（目标 {rate} B/s）");
    }

    /// <summary>
    /// 同步 Read 也必须走异步核心（阻塞等待）并保持进度上报：
    /// ProgressReadStream 的同步入口不允许对内层流做同步 I/O。
    /// </summary>
    [Fact]
    public async Task StreamResponse_ProgressFiresWhileSyncReading()
    {
        const int size = 128 * 1024;
        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();
        var response = await client.GetAsync<Stream>(server.Url + "file", c =>
            c.Callbacks.OnDownloadProgress = events.Add);

        await using var stream = response.Data!;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        Assert.Equal(size, buffer.ToArray().Length);
        Assert.NotEmpty(events);
        Assert.Equal(size, events[^1].Loaded);
        Assert.Equal(size, events[^1].Total);
        Assert.Equal(1d, events[^1].Progress);
    }

    /// <summary>
    /// 限速 + 同步读：限速等待由同步分支的 Thread.Sleep 改为异步核心的 Task.Delay 后，
    /// 阻塞时长与限速语义都必须保持不变。
    /// </summary>
    [Fact]
    public async Task MaxRate_ThrottlesSyncStreamReading()
    {
        const int size = 64 * 1024;
        const long rate = 256 * 1024; // 256 KB/s -> 约 250ms

        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "file", c => c.Limits.MaxRate = rate);

        var stopwatch = Stopwatch.StartNew();
        await using var stream = response.Data!;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        stopwatch.Stop();

        Assert.Equal(size, buffer.ToArray().Length);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 120,
            $"同步读限速未生效：{stopwatch.ElapsedMilliseconds}ms 就读完了 {size} 字节（目标 {rate} B/s）");
    }

    /// <summary>
    /// 限速计时从「首字节到达」起算：拿到响应后调用方先停一会儿再读，这段停顿不能被当成已消耗的限速预算。
    /// 旧实现把计量器构造时刻当起点，停顿越久限速越弱（CI 上线程池排队 190ms 时，64KB 只用 62ms 就读完；
    /// 停顿超过整段应耗时后限速等于没有）。
    /// </summary>
    [Fact]
    public async Task MaxRate_SyncReading_DoesNotCreditIdleBeforeFirstRead()
    {
        const int size = 64 * 1024;
        const long rate = 256 * 1024; // 256 KB/s -> 约 250ms

        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "file", c => c.Limits.MaxRate = rate);
        await using var stream = response.Data!;

        // 模拟「响应已就绪，但调用方隔了一段时间才开始读」。
        await Task.Delay(190);

        var stopwatch = Stopwatch.StartNew();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        stopwatch.Stop();

        Assert.Equal(size, buffer.ToArray().Length);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 120,
            $"读前停顿抵掉了限速预算：{stopwatch.ElapsedMilliseconds}ms 就读完了 {size} 字节（目标 {rate} B/s）");
    }

    [Fact]
    public async Task MaxRate_ThrottlesUpload()
    {
        const int size = 64 * 1024;
        const long rate = 512 * 1024;

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var stopwatch = Stopwatch.StartNew();
        await client.PostAsync(server.Url + "upload", new byte[size], c => c.Limits.MaxRate = rate);
        stopwatch.Stop();

        Assert.Equal(size, server.Received[0].Body.Length);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 80,
            $"上传限速未生效：{stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DownloadProgress_FinalHundredPercentReportedExactlyOnce()
    {
        const int size = 96 * 1024;
        await using var server = TestHttpServer.Start(_ => Binary(size));
        using var client = new AxiosClient();

        var done = new List<AxiosProgressEvent>();

        await client.GetAsync<byte[]>(server.Url + "file", c =>
            c.Callbacks.OnDownloadProgress = e =>
            {
                if (e.Loaded >= size)
                {
                    done.Add(e);
                }
            });

        // EOF 与「读满声明长度」不能各报一次 100%
        Assert.Single(done);
        Assert.Equal(1d, done[0].Progress);
    }

    [Fact]
    public async Task UploadProgress_FinalHundredPercentReportedExactlyOnce()
    {
        const int size = 96 * 1024;
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var done = new List<AxiosProgressEvent>();

        await client.PostAsync(server.Url + "upload", new byte[size], c =>
            c.Callbacks.OnUploadProgress = e =>
            {
                if (e.Loaded >= size)
                {
                    done.Add(e);
                }
            });

        Assert.Single(done);
        Assert.Equal(1d, done[0].Progress);
    }

    [Fact]
    public void ProgressEvent_ToString_IncludesDirectionAndPercent()
    {
        var upload = new AxiosProgressEvent(512, 2048, 1024, TimeSpan.FromSeconds(1), upload: true);
        var download = new AxiosProgressEvent(2048, 2048, null, null, upload: false);

        Assert.Equal("upload 512/2048 bytes (25.0%)", upload.ToString());
        Assert.Equal("download 2048/2048 bytes (100.0%)", download.ToString());
        Assert.Equal(0.25d, upload.Progress);
        Assert.Equal(TimeSpan.FromSeconds(1), upload.Estimated);
        Assert.True(upload.Upload);
        Assert.False(download.Upload);
    }

    [Fact]
    public void ProgressEvent_UnknownTotal_HasNoProgress()
    {
        var e = new AxiosProgressEvent(100, null, 50, null, upload: false);

        Assert.Null(e.Progress);
        Assert.Null(e.Total);
        Assert.Equal("download 100/? bytes (?%)", e.ToString());
    }

    [Fact]
    public async Task Progress_WithoutCallbacks_DoesNotWrapContent()
    {
        // 没有回调也没有限速时应走原路径（这里只验证功能不受影响）
        await using var server = TestHttpServer.Start(_ => Binary(1024));
        using var client = new AxiosClient();

        var response = await client.GetAsync<byte[]>(server.Url + "file");

        Assert.Equal(1024, response.Data!.Length);
    }


    /// <summary>声明任意 <c>Content-Length</c>，但始终写出完整正文的 Handler（模拟声明不可信）。</summary>
    private sealed class FixedLengthHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long _declared;

        public FixedLengthHandler(byte[] body, long declaredLength)
        {
            _body = body;
            _declared = declaredLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentLength = _declared;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private static byte[] Body(TestRequest request) => request.Body;

    // ===============================================================
    // 测试夹具
    // ===============================================================


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


    private static TestResponse Binary(int size)
        => new(Body: new byte[size], ContentType: "application/octet-stream");
}