using System.Net;
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 第四轮全局排查（TDD）：先用失败用例钉死每条缺陷的<b>期望行为</b>，再改实现。
/// <para>
/// 覆盖：Defaults 的 *Set 标记泄漏到生效配置、上传侧 I/O 异常被误译、重发正文的进度终值、
/// 表单非 UTF-8 百分号编码、Content-Length 的头顺序依赖、[ApiIgnore] 覆盖继承绑定、
/// Path/Header 的标量文本化、Auth 无法按请求清除、空白 Url。
/// </para>
/// </summary>
public sealed class Audit20261022Round4Tests
{
    private static byte[] Body(TestRequest request) => request.Body;

    // ===============================================================
    // 1. 构造后改 Defaults.MaxRedirects：文档承诺「不生效、不报错」，
    //    实际每个请求都在 ValidateMaxRedirects 抛 ERR_BAD_OPTION_VALUE。
    //    根因：MergeConfig / Shortcut 把 Defaults 的 MaxRedirectsSet 一起搬进了生效配置，
    //    校验层无法区分「本次按请求写的」与「从实例默认继承来的」。
    // ===============================================================

    [Fact]
    public async Task DefaultsMaxRedirectsChangedAfterConstruction_DoesNotFailRequests()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // AxiosClient.Defaults 的 XML 文档：连接级选项构造后修改不生效，但不会有任何报错。
        client.Defaults.MaxRedirects = 3;

        var response = await client.GetAsync(server.Url + "items");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task DefaultsMaxRedirectsViaRequestAsync_DoesNotFailRequestsEither()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 构造之后再改：与快捷方法入口是同一个根因（*Set 标记随 Clone 泄漏进生效配置）
        client.Defaults.MaxRedirects = 2;

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "items",
            Method = HttpMethod.Get
        });

        Assert.Equal(200, response.Status);
    }

    // ===============================================================
    // 2. 上传正文写失败（网络 I/O）被 ProgressHttpContent 误译成
    //    「OnUploadProgress 回调抛出异常」+ ERR_BAD_REQUEST；期望 ERR_NETWORK。
    // ===============================================================

    [Fact]
    public async Task UploadBodyWriteFailure_MapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new FailingBodyWriteHandler(), disposeHandler: false);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://localhost/upload", new { a = 1 }, c => c.OnUploadProgress = _ => { }));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    [Fact]
    public async Task UploadBodyWriteFailure_WithoutProgressCallback_StillMapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new FailingBodyWriteHandler(), disposeHandler: false);

        // 只有 MaxRate 没有回调：旧实现仍报「OnUploadProgress 回调抛出异常」，文案完全失真。
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://localhost/upload", new { a = 1 }, c => c.MaxRate = 1_000_000));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.DoesNotContain("OnUploadProgress", ex.Message);
    }

    // ===============================================================
    // 3. 307 重发正文：累计进度的终值必须覆盖两轮（旧实现在第二轮第一个事件就锁死）。
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
            c.OnUploadProgress = e => events.Add(e.Loaded);
        });

        Assert.Equal(2, server.Received.Count);
        Assert.NotEmpty(events);

        // 两轮各写一次正文 → 累计字节数必须是 2×payload
        Assert.Equal(2L * size, events[^1]);
    }

    // ===============================================================
    // （已排除）表单非 UTF-8 百分号编码：逐字节直出可打印 ASCII 是字节级可逆的
    // （big5 的 "三" = A4 54 编码成 %A4T，解码回来仍是 A4 54），不是缺陷。
    // 强行"按字符"改写会让 UTF-16 等编码的 ASCII 字符丢掉尾字节（k%00 → k），反而损坏键名。
    // ===============================================================

    // ===============================================================
    // 5. Content-Length 校验不得依赖头的插入顺序（无正文时应一律跳过）。
    // ===============================================================

    [Fact]
    public async Task ExplicitContentLength_WithoutBody_IsIgnoredRegardlessOfHeaderOrder()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 先写 Content-Type：旧实现会先造一个 0 字节正文，随后 Content-Length 被判成「不一致」而抛错。
        await client.PostAsync(server.Url + "bodyless", (object?)null, c =>
        {
            c.Headers.Set("Content-Type", "application/json");
            c.Headers.Set("Content-Length", "5");
        });

        Assert.Equal("0", server.Received[0].Headers.GetValueOrDefault("content-length"));
    }

    // ===============================================================
    // 6. 派生类用 [ApiIgnore] 覆盖基类带 [Query] 的 virtual 属性：
    //    语义是「完全不绑定」，旧实现误判成「与参数特性同用的二义性」。
    // ===============================================================

    [Fact]
    public async Task DerivedApiIgnore_OverridesInheritedQueryBinding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new IgnoredInheritedPageRequest { Page = 7 });

        var received = Assert.Single(server.Received);
        Assert.Null(received.Query);
    }

    // ===============================================================
    // 7. [Path] / [Header] 的标量文本化必须与 [Query] 一致：bool 是 true/false（不是 True/False），
    //    日期时间是 ISO 8601（不是不变文化的短日期 + 空格）。
    // ===============================================================

    [Fact]
    public async Task PathAndHeader_ScalarText_MatchesQueryConvention()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var when = new DateTime(2026, 10, 22, 8, 30, 0, DateTimeKind.Utc);

        await client.RequestAsync(new ScalarTextRequest { Flag = true, When = when, DryRun = false });

        var received = Assert.Single(server.Received);

        Assert.Equal("/flags/true", received.Path);
        Assert.Equal("false", received.Headers.GetValueOrDefault("x-dry-run"));

        // Query 侧既有约定：DateTime → UTC 毫秒 ISO 8601
        Assert.Contains("When=2026-10-22T08%3A30%3A00.000Z", received.Query);
    }

    // ===============================================================
    // 8. 按请求显式写 Auth = null 必须能关掉实例级 Basic 认证（否则凭据会静默发到第三方端点）。
    // ===============================================================

    [Fact]
    public async Task RequestScopedNullAuth_ClearsInstanceAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { Auth = new AxiosAuth("user", "pass") });

        await client.RequestAsync(new AxiosConfig { Url = server.Url + "public", Auth = null });

        Assert.False(server.Received[0].Headers.ContainsKey("authorization"));
    }

    // ===============================================================
    // 9. RequestAsync 的空白 Url 不得静默打到 baseAddress（与快捷方法的 fail-fast 一致）。
    // ===============================================================

    [Fact]
    public async Task RequestAsync_BlankUrl_ThrowsInvalidUrl()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig { Url = "   ", Method = HttpMethod.Post }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    // ===============================================================
    // 10. 表单通道承载文件部件：属于调用方配置错误，错误码应为 ERR_BAD_OPTION_VALUE
    //     （与 multipart 侧一致），不是 ERR_BAD_REQUEST。
    // ===============================================================

    [Fact]
    public async Task FormUrlEncoded_FilePart_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(
                server.Url + "upload",
                new { file = new MemoryStream(new byte[4]) },
                c => c.ContentType = AxiosConfig.FormUrlEncodedContentType));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ===============================================================
    // 11. 响应正文读取阶段的 HttpRequestException 也必须归一到 ERR_NETWORK
    //     （类注释承诺「统一正文阶段的取消 / IO 异常」，实际只 catch 了 IOException）。
    // ===============================================================

    [Fact]
    public async Task ResponseBodyReadFailure_MapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new ThrowingReadHandler(), disposeHandler: false);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://localhost/data"));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    // ===============================================================
    // 测试夹具
    // ===============================================================

    /// <summary>读到正文时写入一个「写就失败」的流，等价于连接在发送正文途中被断开。</summary>
    private sealed class FailingBodyWriteHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(new FailingWriteStream(), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("发送正文时连接被断开（模拟）");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("发送正文时连接被断开（模拟）"));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("发送正文时连接被断开（模拟）"));
    }

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
}

/// <summary>基类带查询绑定，派生类用 [ApiIgnore] 覆盖：该属性应完全不参与绑定。</summary>
public class PagedRequestBase
{
    [Query("page")]
    public virtual int Page { get; set; }
}

[Get("items")]
public sealed class IgnoredInheritedPageRequest : PagedRequestBase, IApiRequest<Person>
{
    [ApiIgnore]
    public override int Page { get; set; }
}

/// <summary>同一个值分别落在路径 / 请求头 / 查询串，用于校验三处文本化一致。</summary>
[Get("flags/{flag}")]
public sealed class ScalarTextRequest : IApiRequest<Person>
{
    [Path]
    public bool Flag { get; set; }

    [Header("X-Dry-Run")]
    public bool DryRun { get; set; }

    [Query]
    public DateTime When { get; set; }
}
