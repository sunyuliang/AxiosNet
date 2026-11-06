using System.Text;
using AxiosNet.Internal;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 第 2 批对抗性测试：快捷方法 / 通用入口的配置组装、拦截器前后授权时点、ContentType 双入口与 boundary、
/// 取消分类两条路径一致性、失败分类共享表、字符集与 BOM 口径。
/// </summary>
public sealed class Batch2AdversarialTests
{
    // ─────────────────── 1. 快捷方法 vs 通用入口：四个叶子的三种写法 ───────────────────

    [Fact]
    public async Task Body_ExplicitNull_ClearsInShortcutAndGenericConfigPaths()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Body = "instance-secret" });

        await client.GetAsync(server.Url + "shortcut", data: null);
        await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "generic",
            Method = HttpMethod.Get,
            Body = null,
        });

        var received = server.Received;
        Assert.Equal(2, received.Count);
        Assert.Empty(received[0].Body);
        Assert.Empty(received[1].Body);
    }

    [Fact]
    public async Task Body_Omitted_InheritsInstanceDefault_InBothPaths()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Body = "instance-secret" });

        await client.GetAsync(server.Url + "shortcut");
        await client.RequestAsync(new AxiosConfig { Url = server.Url + "generic", Method = HttpMethod.Get });

        var received = server.Received;
        Assert.Equal("instance-secret", Encoding.UTF8.GetString(received[0].Body));
        Assert.Equal("instance-secret", Encoding.UTF8.GetString(received[1].Body));
    }

    [Fact]
    public async Task CancellationToken_ExplicitNone_ClearsInheritedToken_InAllThreeForms()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            CancellationToken = canceled.Token,
        });

        // 1) 快捷方法的显式令牌重载（tokenProvided: true）
        await client.GetAsync("shortcut", CancellationToken.None);

        // 2) 通用 Action 入口显式写 None
        await client.RequestAsync(c =>
        {
            c.Url = "generic-action";
            c.Method = HttpMethod.Get;
            c.CancellationToken = CancellationToken.None;
        });

        // 3) 通用配置对象显式写 None
        await client.RequestAsync(new AxiosConfig
        {
            Url = "generic-config",
            Method = HttpMethod.Get,
            CancellationToken = CancellationToken.None,
        });

        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task CancellationToken_Omitted_InheritsInstanceDefault_InAllThreeForms()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            CancellationToken = canceled.Token,
        });

        var shortcut = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("shortcut"));
        var action = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(c =>
        {
            c.Url = "generic-action";
            c.Method = HttpMethod.Get;
        }));
        var config = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig { Url = "generic-config", Method = HttpMethod.Get }));

        Assert.Equal(AxiosException.ErrCanceled, shortcut.Code);
        Assert.Equal(AxiosException.ErrCanceled, action.Code);
        Assert.Equal(AxiosException.ErrCanceled, config.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ContentType_ExplicitNull_IsRejectedInAllForms()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.PostAsync("shortcut", "x", c => c.ContentType = null!));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.RequestAsync(c =>
        {
            c.Url = "generic-action";
            c.Method = HttpMethod.Post;
            c.ContentType = null!;
        }));

        Assert.Throws<ArgumentNullException>(() => new AxiosConfig { ContentType = null! });

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Compression_ExplicitNull_ClearsInShortcutAndGenericAction()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Compression = AxiosCompression.Gzip,
        });

        await client.PostAsync("shortcut", "compressible payload compressible payload", c => c.Compression = null);
        await client.RequestAsync(c =>
        {
            c.Url = "generic-action";
            c.Method = HttpMethod.Post;
            c.Body = "compressible payload compressible payload";
            c.Compression = null;
        });

        var received = server.Received;
        Assert.False(received[0].Headers.ContainsKey("content-encoding"));
        Assert.False(received[1].Headers.ContainsKey("content-encoding"));
    }

    [Fact]
    public async Task Compression_ExplicitNull_ClearsInGenericConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Compression = AxiosCompression.Gzip,
        });

        // 与快捷方法 / Action 入口同一个「显式 null = 关掉实例默认压缩」语义。
        await client.RequestAsync(new AxiosConfig
        {
            Url = "generic-config",
            Method = HttpMethod.Post,
            Body = "compressible payload compressible payload",
            Compression = null,
        });

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("content-encoding"));
    }

    [Fact]
    public async Task Compression_Omitted_InheritsInstanceDefault_InAllForms()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Compression = AxiosCompression.Gzip,
        });

        await client.PostAsync("shortcut", "compressible payload compressible payload");
        await client.RequestAsync(c =>
        {
            c.Url = "generic-action";
            c.Method = HttpMethod.Post;
            c.Body = "compressible payload compressible payload";
        });
        await client.RequestAsync(new AxiosConfig
        {
            Url = "generic-config",
            Method = HttpMethod.Post,
            Body = "compressible payload compressible payload",
        });

        var received = server.Received;
        Assert.Equal("gzip", received[0].Headers["content-encoding"]);
        Assert.Equal("gzip", received[1].Headers["content-encoding"]);
        Assert.Equal("gzip", received[2].Headers["content-encoding"]);
    }

    [Fact]
    public async Task Compression_None_ClearsInGenericConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Compression = AxiosCompression.Gzip,
        });

        await client.RequestAsync(new AxiosConfig
        {
            Url = "generic-config",
            Method = HttpMethod.Post,
            Body = "compressible payload compressible payload",
            Compression = AxiosCompression.None,
        });

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("content-encoding"));
    }

    // ─────────────────── 2. 拦截器前后授权校验时点 ───────────────────

    [Fact]
    public async Task Interceptor_ClearsSlot_SuppressesAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Transport = new() { Auth = AxiosCredential.Bearer("old") },
        });
        client.Interceptors.Request.Use(
            (Func<AxiosConfig, Task<AxiosConfig>>)(config =>
            {
                config.Transport.Auth = null;
                return Task.FromResult(config);
            }));

        await client.GetAsync("x");

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task Interceptor_SwapsSlot_ResolvesNewCredential()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Transport = new() { Auth = AxiosCredential.Bearer("old") },
        });
        client.Interceptors.Request.Use(
            (Func<AxiosConfig, Task<AxiosConfig>>)(config =>
            {
                config.Transport.Auth = AxiosCredential.Bearer("new");
                return Task.FromResult(config);
            }));

        await client.GetAsync("x");

        Assert.Equal("Bearer new", Assert.Single(server.Received).Headers["authorization"]);
    }

    [Fact]
    public async Task Interceptor_AddsHeaderWithoutCredentialSource_IsSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Interceptors.Request.Use(
            (Func<AxiosConfig, Task<AxiosConfig>>)(config =>
            {
                config.Headers.Set("Authorization", "Bearer manual");
                return Task.FromResult(config);
            }));

        await client.GetAsync("x");

        Assert.Equal("Bearer manual", Assert.Single(server.Received).Headers["authorization"]);
    }

    // ─────────────────── 3. ContentType 双入口与 multipart boundary ───────────────────

    [Theory]
    [InlineData("multipart/form-data; boundary = abc")]
    [InlineData("multipart/form-data; boundary=\"abc\"")]
    [InlineData("multipart/form-data; charset=utf-8; boundary=abc")]
    [InlineData("multipart/form-data; boundary=abc; charset=utf-8")]
    public async Task Multipart_ExplicitBoundary_HeaderAndBodyStayConsistent(string contentType)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostFormAsync("x", new Person("alice", 42), c => c.ContentType = contentType);

        var sent = Assert.Single(server.Received);
        var header = sent.Headers["content-type"];

        Assert.Equal("abc", RequestContentType.ExtractBoundary(header));

        var body = Encoding.UTF8.GetString(sent.Body);
        Assert.StartsWith("--abc\r\n", body);
        Assert.EndsWith("--abc--\r\n", body);
    }

    [Fact]
    public async Task Multipart_HeaderMediaTypeConflictsWithConfig_KeepsBoundaryConsistency()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostFormAsync("x", new Person("alice", 42), c =>
        {
            c.ContentType = "multipart/form-data; boundary=abc";
            // 与 ContentType 的媒体类型不一致：multipart 的 boundary 保护优先，不能被头覆盖成不可解析的 JSON。
            c.Headers.Set("Content-Type", "application/json");
        });

        var sent = Assert.Single(server.Received);
        var header = sent.Headers["content-type"];

        Assert.Equal(AxiosContentType.MultipartFormData, header.Split(';')[0].Trim());
        Assert.Equal("abc", RequestContentType.ExtractBoundary(header));
        Assert.StartsWith("--abc\r\n", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task NonMultipart_HeaderMediaTypeOverride_DoesNotChangeBodyEncoding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync("x", new Person("alice", 42), c =>
        {
            c.ContentType = "application/json";
            // 只改线上媒体类型（自定义 JSON 子类型），正文仍是 JSON 编码。
            c.Headers.Set("Content-Type", "application/vnd.api+json");
        });

        var sent = Assert.Single(server.Received);
        Assert.Equal("application/vnd.api+json", sent.Headers["content-type"]);
        Assert.StartsWith("{", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task TextualContentTypeWithoutCharset_DoesNotAppendCharset()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync("x", "hello", c => c.ContentType = "text/plain");

        var sent = Assert.Single(server.Received);
        // 未设置 RequestEncoding 时不补 charset：声明与默认 UTF-8 一致，不额外写参数。
        Assert.Equal("text/plain", sent.Headers["content-type"]);
        Assert.Equal("hello", Encoding.UTF8.GetString(sent.Body));
    }

    // ─────────────────── 4. 取消分类：两条路径一致 ───────────────────

    [Theory]
    [InlineData(false, false, false, "None")]
    [InlineData(true, false, false, "Caller")]
    [InlineData(false, true, false, "Canceled")]
    [InlineData(false, false, true, "Timeout")]
    [InlineData(true, true, true, "Caller")]
    [InlineData(false, true, true, "Canceled")]
    [InlineData(true, true, false, "Caller")]
    [InlineData(true, false, true, "Caller")]
    public void Classify_PriorityIsCallerThenCanceledThenTimeout(
        bool caller,
        bool external,
        bool timeout,
        string expected)
    {
        using var callerCts = new CancellationTokenSource();
        using var externalCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource();

        if (caller)
        {
            callerCts.Cancel();
        }

        if (external)
        {
            externalCts.Cancel();
        }

        if (timeout)
        {
            timeoutCts.Cancel();
        }

        Assert.Equal(
            expected,
            CancellationClassifier.Classify(externalCts.Token, timeoutCts.Token, callerCts.Token).ToString());
    }

    [Fact]
    public async Task StreamRead_BudgetExhausted_WithCallerToken_IsCanceled()
    {
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        using var stream = ExhaustedStream(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => stream.ReadAsync(new byte[8], 0, 8, callerCts.Token));

        // 调用方自备令牌取消：归属仍是 Caller（优先级最高），但最终出口与普通请求路径一致。
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    [Fact]
    public async Task StreamRead_BudgetExhausted_WithExternalCanceled_IsCanceled()
    {
        using var externalCts = new CancellationTokenSource();
        externalCts.Cancel();
        using var stream = ExhaustedStream(externalCts.Token);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    [Fact]
    public async Task StreamRead_BudgetExhausted_NoToken_IsTimeout()
    {
        using var stream = ExhaustedStream(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    [Fact]
    public async Task StreamRead_InnerOceWithoutKnownToken_IsNormalizedToCanceled()
    {
        using var stream = new TimeoutStream(
            new OperationCanceledStream(),
            remaining: null,
            externalToken: CancellationToken.None,
            new AxiosConfig(),
            new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/"));

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None));

        // 没有已知令牌的 OCE：与普通请求路径一样归一为 ERR_CANCELED，不能被误报成超时 / 网络失败。
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    [Fact]
    public async Task BufferedRequest_OceWithoutKnownToken_IsNormalizedToCanceled()
    {
        using var client = new AxiosClient(handler: new OperationCanceledHandler());

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("http://example.invalid/"));

        // 普通请求路径与流读路径的同一契约：不是超时的 OCE 一律归一为 ERR_CANCELED
        // （不能被误报成解析 / 网络失败，见上一条用例）。
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        // net6 的 HttpClient 会把处理器抛出的 OCE 收成 TaskCanceledException（仍是 OCE 家族）。
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    // ─────────────────── 5. 失败分类共享表 ───────────────────

    [Theory]
    [InlineData("network", true)]
    [InlineData("conn-aborted", true)]
    [InlineData("http-request", true)]
    [InlineData("io", true)]
    [InlineData("canceled", false)]
    [InlineData("oce", false)]
    [InlineData("circuit-open", false)]
    [InlineData("bad-response", false)]
    [InlineData("bad-option", false)]
    [InlineData("pool-exhausted", false)]
    public void IsNetworkFailure_OnlyTransientNetworkLayer(string kind, bool expected)
    {
        Assert.Equal(expected, FailureClassifier.IsNetworkFailure(CreateException(kind)));
    }

    [Theory]
    [InlineData("canceled", true)]
    [InlineData("oce", true)]
    [InlineData("network", false)]
    [InlineData("conn-aborted", false)]
    [InlineData("http-request", false)]
    [InlineData("io", false)]
    [InlineData("circuit-open", false)]
    public void IsCancellation_OnlyCancelShapes(string kind, bool expected)
    {
        Assert.Equal(expected, FailureClassifier.IsCancellation(CreateException(kind)));
    }

    [Fact]
    public void IsCancel_MirrorsFailureClassifier()
    {
        Assert.True(Axios.IsCancel(CreateException("canceled")));
        Assert.True(Axios.IsCancel(CreateException("oce")));
        Assert.False(Axios.IsCancel(CreateException("network")));
        Assert.False(Axios.IsCancel(CreateException("circuit-open")));
        Assert.False(Axios.IsCancel(null));
        Assert.False(FailureClassifier.IsNetworkFailure(null));
    }

    // ─────────────────── 6. EncodingResolver / BomDetection ───────────────────

    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData(" utf-8 ")]
    [InlineData("\"utf-8\"")]
    [InlineData("us-ascii")]
    [InlineData("iso-8859-1")]
    [InlineData("utf-16")]
    public void CharsetResolution_RequestAndResponseSidesAgree(string name)
    {
        var responseEncoding = ResponseTextDecoder.ResolveFromCharset(name);
        Assert.NotNull(responseEncoding);

        var requestEncoding = RequestContentType.ResolveCharset("text/plain; charset=" + name, null);
        Assert.NotNull(requestEncoding);
        Assert.Equal(responseEncoding.CodePage, requestEncoding.CodePage);
        Assert.Equal(responseEncoding.CodePage, EncodingResolver.TryResolve(name)!.CodePage);
    }

    [Fact]
    public void UnknownCharset_RequestThrows_ResponseFallsBack()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            RequestContentType.ResolveCharset("text/plain; charset=x-unknown-charset", null));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);

        // 接收侧把未知 charset 当没声明：回落到 BOM / UTF-8，绝不抛。
        Assert.Null(ResponseTextDecoder.ResolveFromCharset("x-unknown-charset"));
        Assert.Equal("héllo", ResponseTextDecoder.Decode(Encoding.UTF8.GetBytes("héllo"), "x-unknown-charset", null));
    }

    [Fact]
    public void BomDetection_AgreesWithResponseBomProbe()
    {
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D };
        Assert.True(BomDetection.HasUtf8Bom(utf8));
        Assert.Equal(Encoding.UTF8.CodePage, ResponseTextDecoder.DetectFromBom(utf8)!.CodePage);

        var utf16Le = new byte[] { 0xFF, 0xFE, 0x41, 0x00 };
        Assert.False(BomDetection.HasUtf8Bom(utf16Le));
        Assert.Equal(Encoding.Unicode.CodePage, ResponseTextDecoder.DetectFromBom(utf16Le)!.CodePage);

        var utf32Le = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00 };
        Assert.False(BomDetection.HasUtf8Bom(utf32Le));
        Assert.Equal(new UTF32Encoding(false, true).CodePage, ResponseTextDecoder.DetectFromBom(utf32Le)!.CodePage);

        Assert.False(BomDetection.HasUtf8Bom(new byte[] { 0xEF, 0xBB }));
        Assert.False(BomDetection.HasUtf8Bom(ReadOnlySpan<byte>.Empty));
        Assert.Null(ResponseTextDecoder.DetectFromBom(new byte[] { 0x7B, 0x7D }));
    }

    // ─────────────────── helpers ───────────────────

    private static Exception CreateException(string kind)
    {
        var config = new AxiosConfig();
        return kind switch
        {
            "network" => AxiosException.Network(config, null, new HttpRequestException("boom")),
            "conn-aborted" => AxiosException.Timeout(config, null!, null),
            "http-request" => new HttpRequestException("boom"),
            "io" => new IOException("boom"),
            "canceled" => AxiosException.Canceled(config, null),
            "oce" => new OperationCanceledException(),
            "circuit-open" => AxiosException.CircuitOpen(config, null, "example.com", TimeSpan.FromSeconds(1)),
            "bad-response" => AxiosException.BadResponse(config, null!, "T", new InvalidOperationException()),
            "bad-option" => AxiosException.InvalidOption("boom"),
            "pool-exhausted" => AxiosException.PoolExhausted(config, null, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static TimeoutStream ExhaustedStream(CancellationToken externalToken)
        => new(
            new OperationCanceledStream(),
            remaining: TimeSpan.Zero,
            externalToken,
            new AxiosConfig(),
            new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/"));

    private sealed class OperationCanceledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new OperationCanceledException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OperationCanceledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new OperationCanceledException());
    }
}
