using System.Diagnostics;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// P1：上传/下载进度回调与限速（maxRate）。
/// </summary>
public sealed class ProgressAndRateTests
{
    private static TestResponse Binary(int size)
        => new(Body: new byte[size], ContentType: "application/octet-stream");

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
}
