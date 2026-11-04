using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AxiosNet.Cache;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Resilience;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 本轮对抗性检查的临时用例：只用于真实跑出「输入 → 预期 → 实测」，跑完后按结论决定去留。
/// </summary>
public sealed class AdversarialChecksTests
{
    // ---------------------------------------------------------------
    // 攻击 1：Response 回填的归属与字段一致性
    // ---------------------------------------------------------------

    /// <summary>重试全部失败：抛的是最后一次尝试，Response 的 status / headers / request / rawText 必须与异常自身一致。</summary>
    [Fact]
    public async Task A1_RetryAllFail_ResponseMatchesFinalAttempt()
    {
        await using var server = TestHttpServer.Start(request =>
        {
            var attempt = request.Headers["X-Attempt"];
            return new TestResponse(
                500,
                Body: Encoding.UTF8.GetBytes("{\"attempt\":\"" + attempt + "\"}"),
                Headers: new Dictionary<string, string> { ["X-Attempt"] = attempt, ["X-Echo"] = "attempt-" + attempt });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) }
        });

        var attempt = 0;
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("fail", c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.TryAddWithoutValidation("X-Attempt", (++attempt).ToString());
                return default;
            }));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(500, ex.Status);
        Assert.NotNull(ex.Response);

        // 字段一致性，不只看非 null。
        Assert.Equal(500, ex.Response!.Status);
        Assert.Equal(500, ex.Status);
        Assert.Same(ex.Request, ex.Response.Request);
        Assert.Equal("3", ex.Response.GetHeader("X-Attempt"));
        Assert.Equal("attempt-3", ex.Response.GetHeader("X-Echo"));
        Assert.Contains("\"attempt\":\"3\"", ex.Response.RawText);

        // 三次尝试都到了服务端；异常只带最后一次。
        Assert.Equal(3, server.Received.Count);
    }

    /// <summary>对冲全部分支失败：异常带着「与异常同一个 request」的那份响应，不能串到别的分支。</summary>
    [Fact]
    public async Task A1_HedgeAllFail_ResponseBelongsToExceptionRequest()
    {
        await using var server = TestHttpServer.Start(request =>
        {
            var branch = request.Headers["X-Branch"];
            return new TestResponse(
                500,
                Body: Encoding.UTF8.GetBytes("{\"branch\":\"" + branch + "\"}"),
                Headers: new Dictionary<string, string> { ["X-Branch"] = branch });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = { MaxHedges = 2, Delay = TimeSpan.Zero }
        });

        var branch = 0;
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PutAsync("hedge", new { ok = true }, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.TryAddWithoutValidation("X-Branch", (++branch).ToString());
                return default;
            }));

        Assert.NotNull(ex.Response);
        Assert.Equal(500, ex.Status);
        Assert.Equal(500, ex.Response!.Status);
        Assert.Same(ex.Request, ex.Response.Request);

        // 异常里的 Request 与 Response 必须来自同一分支：否则就是「一条分支的头 + 另一条分支的响应体」。
        var branchOnRequest = ex.Request!.Headers.GetValues("X-Branch").Single();
        Assert.Equal(branchOnRequest, ex.Response.GetHeader("X-Branch"));
        Assert.Contains("\"branch\":\"" + branchOnRequest + "\"", ex.Response.RawText);
    }

    /// <summary>failover 全端点失败：记录实际带的是第一条失败（文档语义），而非最后端点。</summary>
    [Fact]
    public async Task A1_FailoverAllFail_CarriesFirstFailure()
    {
        await using var primary = TestHttpServer.Start(_ => new TestResponse(
            500,
            Body: Encoding.UTF8.GetBytes("{\"endpoint\":\"primary\"}"),
            Headers: new Dictionary<string, string> { ["X-Endpoint"] = "primary" }));
        await using var mirror = TestHttpServer.Start(_ => new TestResponse(
            500,
            Body: Encoding.UTF8.GetBytes("{\"endpoint\":\"mirror\"}"),
            Headers: new Dictionary<string, string> { ["X-Endpoint"] = "mirror" }));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            primary.Url + "x",
            c => c.Failover.Endpoints = new[] { new Uri(mirror.Url) }));

        Assert.Equal(500, ex.Status);
        Assert.NotNull(ex.Response);

        // 第一条失败（primary）：failover 的文档语义是「全部失败才把第一条失败交给外层」。
        Assert.Equal("primary", ex.Response!.GetHeader("X-Endpoint"));
        Assert.Same(ex.Request, ex.Response.Request);
        Assert.Equal(2, primary.Received.Count + mirror.Received.Count);
    }

    /// <summary>缓存命中的解析失败：Response.Request 必须为 null（真的没发请求），Config 是本次请求的配置。</summary>
    [Fact]
    public async Task A1_CacheHitParseFailure_ResponseRequestNullConfigCurrent()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("{\"name\":\"ada\",\"age\":\"not-an-int\"}"),
            Headers: new Dictionary<string, string> { ["X-Source"] = "cache" }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Cache = new AxiosCache(new AxiosCacheOptions())
        });

        // 先用 string 形态把字节落库（成功），再用 Person 形态命中缓存并在解析期失败。
        await client.GetAsync<string>(server.Url + "x");

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<Person>(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.NotNull(ex.Response);
        Assert.Equal(200, ex.Status);
        Assert.Equal(200, ex.Response!.Status);
        Assert.Null(ex.Response.Request);
        Assert.Null(ex.Request);

        // 命中路径用的是本次请求的生效配置，而不是源实例（也没有别人的配置）。
        Assert.Same(ex.Config, ex.Response.Config);
        Assert.Equal("cache", ex.Response.GetHeader("X-Source"));
        Assert.Single(server.Received);
    }

    /// <summary>回放 response.Config 后失败：Response.Config 必须是目标实例这次生效的配置，不能是源实例对象。</summary>
    [Fact]
    public async Task A1_ReplayedConfigFailure_ResponseConfigIsTargetInstance()
    {
        await using var sourceServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 1)));
        await using var targetServer = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("{\"name\":\"ada\",\"age\":\"bad\"}"),
            Headers: new Dictionary<string, string> { ["X-Instance"] = "target" }));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = sourceServer.Url });
        var first = await source.GetAsync<Person>("x");
        Assert.NotNull(first.Config);

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = targetServer.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            target.RequestAsync<Person>(first.Config!));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.NotNull(ex.Response);
        Assert.Equal(200, ex.Response!.Status);
        Assert.Equal(200, ex.Status);

        // 回放不是扛着源实例的配置对象走：Config 是目标实例本次合并出来的那一份。
        Assert.NotSame(first.Config, ex.Response.Config);
        Assert.Same(ex.Config, ex.Response.Config);
        Assert.Equal(targetServer.Url.TrimEnd('/'), ex.Response.Config!.BaseAddress!.TrimEnd('/'));
        Assert.Equal("target", ex.Response.GetHeader("X-Instance"));

        // 请求确实发到了目标实例的地址。
        Assert.NotNull(ex.Response.Request);
        Assert.Equal(targetServer.Url.TrimEnd('/'), ex.Response.Request!.RequestUri!.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>异常对象被复用后再抛：既有 Response 不会被新请求覆盖（取证「串值」的实际表现）。</summary>
    [Fact]
    public async Task A2_ReusedException_KeepsStaleResponse()
    {
        await using var first = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("{\"name\":\"ada\",\"age\":\"bad\"}"),
            Headers: new Dictionary<string, string> { ["X-Source"] = "first" }));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        var firstFailure = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync<Person>(first.Url + "x"));

        Assert.NotNull(firstFailure.Response);
        Assert.Equal("first", firstFailure.Response!.GetHeader("X-Source"));

        // 第二次请求的 transformResponse 直接把这个旧异常对象再抛出去（模拟调用方缓存异常）。
        var reuse = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(second.Url + "y", c =>
            c.TransformResponse.Add((_, _) => throw firstFailure)));

        Assert.Same(firstFailure, reuse);
        Assert.Equal("first", reuse.Response!.GetHeader("X-Source"));

        // 取证结论：Response 是旧请求的；库内不覆盖已非 null 的 Response 是有意的防重复回填。
        Assert.NotEqual(second.Url + "y", reuse.Request?.RequestUri?.AbsoluteUri);
    }

    // ---------------------------------------------------------------
    // 攻击 2：回填实现本身（并发写 / 重复调用 / 复用）
    // ---------------------------------------------------------------

    /// <summary>对冲分支并发失败：每条分支各自一份异常与响应，不能出现「两条分支共享同一个 Response 对象」。</summary>
    [Fact]
    public async Task A2_ConcurrentHedgeFailures_HaveDistinctResponses()
    {
        await using var server = TestHttpServer.Start(request =>
            new TestResponse(503, Body: Encoding.UTF8.GetBytes(request.Headers["X-Branch"])));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Hedging = { MaxHedges = 3, Delay = TimeSpan.Zero }
        });

        var body = new StringBuilder();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PutAsync("hedge", new { ok = true }, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                request.Headers.TryAddWithoutValidation("X-Branch", Guid.NewGuid().ToString("N"));
                return default;
            }));

        Assert.NotNull(ex.Response);
        Assert.NotNull(ex.Response!.Request);

        // Response 的 Request 与异常的 Request 是同一个对象，且该请求只被这一条分支用过。
        Assert.Same(ex.Request, ex.Response.Request);
        _ = body;
    }

    // ---------------------------------------------------------------
    // 攻击 3：HeaderIsolatedContent 的副作用
    // ---------------------------------------------------------------

    /// <summary>已知长度正文：线上字节一致、Content-Length 保持、内容头隔离生效。</summary>
    [Fact]
    public async Task A3_KnownLengthBody_IsByteIdentical()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));

        using var client = new AxiosClient();

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await client.PostAsync(server.Url + "bytes", new ByteArrayContent(payload), c =>
        {
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                Assert.NotNull(request.Content);
                Assert.Equal(payload.Length, request.Content!.Headers.ContentLength);
                request.Content.Headers.TryAddWithoutValidation("X-Sig", "v");
                return default;
            };
        });

        var received = Assert.Single(server.Received);
        Assert.Equal(payload, received.Body);
        Assert.Equal("5", received.Headers["content-length"]);
        Assert.Equal("v", received.Headers["x-sig"]);
    }

    /// <summary>未知长度正文：外壳 TryComputeLength 返回 false，按 chunked 发出且字节一致。</summary>
    [Fact]
    public async Task A3_UnknownLengthBody_StaysChunked()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));

        using var client = new AxiosClient();

        var payload = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        var stream = new NonSeekableStream(payload);
        var content = new StreamContent(stream);
        content.Headers.ContentLength = null;

        await client.PostAsync(server.Url + "stream", content, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                Assert.Null(request.Content!.Headers.ContentLength);
                return default;
            });

        var received = Assert.Single(server.Received);
        Assert.Equal(payload, received.Body);
        Assert.Equal("chunked", received.Headers["transfer-encoding"]);
    }

    /// <summary>表单 / multipart / 无正文：字节一致、没有正文时不包外壳。</summary>
    [Fact]
    public async Task A3_FormMultipartAndNoBody_AreUnaffected()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));

        using var client = new AxiosClient();

        // form-urlencoded：显式用 FormUrlEncodedContent，避免 PostFormAsync 对复杂值的 multipart 选择干扰形状。
        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("a", "1"),
            new KeyValuePair<string, string>("b", "x")
        });
        await client.PostAsync(server.Url + "form", formContent, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                Assert.NotNull(request.Content);
                return default;
            });

        // multipart：固定 boundary，先算期望字节再发。
        const string boundary = "adv-boundary";
        var form = new MultipartFormDataContent(boundary);
        form.Add(new StringContent("hello", Encoding.UTF8, "text/plain"), "field");

        var expected = await form.ReadAsByteArrayAsync();
        await client.PostAsync(server.Url + "multipart", form, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                Assert.NotNull(request.Content);
                return default;
            });

        // 无正文：钩子看到 Content 为 null。
        await client.GetAsync(server.Url + "empty", c => c.Callbacks.OnSendingRequest = (request, _, _) =>
        {
            Assert.Null(request.Content);
            return default;
        });

        var received = server.Received;
        Assert.Equal(3, received.Count);

        var formBody = received.Single(r => r.Path.EndsWith("form", StringComparison.Ordinal)).Body;
        Assert.Equal("a=1&b=x", Encoding.UTF8.GetString(formBody));

        var multipartBody = received.Single(r => r.Path.EndsWith("multipart", StringComparison.Ordinal)).Body;
        Assert.Equal(expected, multipartBody);

        var empty = received.Single(r => r.Path.EndsWith("empty", StringComparison.Ordinal));
        Assert.Empty(empty.Body);
    }

    /// <summary>压缩正文：外壳复制 Content-Encoding / Content-Length，线上字节与未开钩子时一致。</summary>
    [Fact]
    public async Task A3_CompressedBody_IsByteIdenticalToNoHookPath()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));

        using var client = new AxiosClient();

        var data = new { text = new string('x', 4096) };

        await client.PostAsync(server.Url + "plain", data, c => c.RequestCompression = AxiosCompression.Gzip);
        await client.PostAsync(server.Url + "hooked", data, c =>
        {
            c.RequestCompression = AxiosCompression.Gzip;
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                Assert.Equal("gzip", request.Content!.Headers.ContentEncoding.Single());
                return default;
            };
        });

        var received = server.Received;
        Assert.Equal(2, received.Count);
        Assert.Equal(received[0].Body, received[1].Body);
        Assert.Equal("gzip", received[1].Headers["content-encoding"]);
    }

    /// <summary>上传进度：钩子包一层外壳后回调仍触发，且线上字节不变。</summary>
    [Fact]
    public async Task A3_UploadProgress_StillFiresUnderHook()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));

        using var client = new AxiosClient();

        var payload = new byte[8192];
        var progress = 0L;
        await client.PostAsync(server.Url + "upload", new ByteArrayContent(payload), c =>
        {
            c.Callbacks.OnUploadProgress = args => progress = args.Loaded;
            c.Callbacks.OnSendingRequest = (_, _, _) => default;
        });

        var received = Assert.Single(server.Received);
        Assert.Equal(payload, received.Body);
        Assert.True(progress > 0, "上传进度回调没有触发");
    }

    /// <summary>内层正文的所有权不变：模板内容在请求结束时释放一次，外壳不重复释放。</summary>
    [Fact]
    public async Task A3_InnerContentDisposedExactlyOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        var inner = new TrackingContent(16);
        await client.PostAsync(server.Url + "x", inner, c =>
            c.Callbacks.OnSendingRequest = (_, _, _) => default);

        Assert.Equal(1, inner.DisposeCount);
    }

    // ---------------------------------------------------------------
    // 攻击 6：交叉影响（闸门槽位归还、TotalTimeout）
    // ---------------------------------------------------------------

    /// <summary>钩子失败时并发槽位必须归还：否则同实例的下一条请求会排到 QueueTimeout 超时。</summary>
    [Fact]
    public async Task A6_HookFailure_ReleasesGateSlot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Limits =
            {
                MaxConcurrentRequests = 1,
                QueueTimeout = TimeSpan.FromMilliseconds(300)
            }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("first", c =>
            c.Callbacks.OnSendingRequest = (_, _, _) => throw new InvalidOperationException("boom")));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        // 槽位没泄漏：下一条请求立刻拿到槽位。
        var ok = await client.GetAsync("second");
        Assert.Equal(200, ok.Status);
    }

    // ---------------------------------------------------------------

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent(int length) : base(new byte[length])
        {
        }

        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
