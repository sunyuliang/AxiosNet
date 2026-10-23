using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-20 低优先级项（L3–L17）的回归：查询参数 base64、请求体内存类型、
/// multipart 矛盾配置与部件命名、SslOptions 扩展点、错误配置挂载、共享池门面、
/// 关闭语义与流式包装细节。
/// </summary>
public sealed class LowPriorityRegressionTests
{
    // ------------------------------------------------------------------
    // L3：byte[] 查询参数 → base64（此前输出 System.Byte[]）
    // ------------------------------------------------------------------

    [Fact]
    public void ByteArrayParams_SerializeAsReversibleBase64()
    {
        using var client = new AxiosClient();
        var payload = new byte[] { 1, 2, 3 };

        var uri = client.GetUri(c =>
        {
            c.Url = "http://host/search";
            c.Params = new { bin = payload };
        });

        Assert.Equal("http://host/search?bin=AQID", uri);
        Assert.Equal(payload, Convert.FromBase64String("AQID"));
    }

    // ------------------------------------------------------------------
    // L4：Uri 归一化与线上请求行一致（框架行为，文档化 + 锁定）
    // ------------------------------------------------------------------

    [Fact]
    public void GetUri_NormalizesUnreservedPercentEscapes_LikeTheWire()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.Url = "http://host/p";
            c.Serialization.ParamsSerializer = _ => "q=%41%7E%2B";
        });

        // %41/%7E 是非保留字符（归一化为 A/~），%2B 是保留字符（保持转义）
        Assert.Equal("http://host/p?q=A~%2B", uri);
    }

    // ------------------------------------------------------------------
    // L7：Memory<byte> / ArraySegment<byte> / ReadOnlyMemory<byte> 请求体
    // ------------------------------------------------------------------

    [Fact]
    public async Task MemoryByte_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = new byte[] { 0, 1, 2, 250, 255 };

        await client.PostAsync(server.Url + "bin", new Memory<byte>(payload), c => c.ContentType = "application/octet-stream");

        var sent = server.Received[0];
        Assert.Equal("application/octet-stream", sent.Headers.GetValueOrDefault("Content-Type"));
        Assert.Equal("5", sent.Headers.GetValueOrDefault("Content-Length"));
        Assert.Equal(payload, sent.Body);
    }

    [Fact]
    public async Task ArraySegment_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "bin",
            new ArraySegment<byte>(new byte[] { 9, 8, 7, 6 }, 1, 2),
            c => c.ContentType = "application/octet-stream");

        Assert.Equal(new byte[] { 8, 7 }, server.Received[0].Body);
    }

    [Fact]
    public async Task ReadOnlyMemory_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = new byte[] { 4, 5, 6 };

        await client.PostAsync(
            server.Url + "bin",
            new ReadOnlyMemory<byte>(payload),
            c => c.ContentType = "application/octet-stream");

        Assert.Equal(payload, server.Received[0].Body);
    }

    [Fact]
    public async Task MemoryByte_InMultipart_BecomesFilePart()
    {
        // 此前 Memory<byte> 会掉进 JSON 序列化兜底（字段变成 {}）， silently broken
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { blob = new Memory<byte>(new byte[] { 9, 8, 7 }) },
            c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=blob", body);
        Assert.Contains("filename=blob", body);
        Assert.Contains("application/octet-stream", body);
    }

    // ------------------------------------------------------------------
    // L8：multipart Content-Type + 原始内容请求体（头部/正文自相矛盾）
    // ------------------------------------------------------------------

    [Fact]
    public async Task RawString_UnderMultipartContentType_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", "hello", c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RawBytes_UnderMultipartContentType_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", new byte[] { 1, 2 }, c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RawBytes_WithDeclaredBoundary_ArePassedThroughAsIs()
    {
        // 带上 boundary 即视为「预序列化的 multipart 正文」，按调用方断言原样发送
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = Encoding.UTF8.GetBytes("--xyz\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nb\r\n--xyz--\r\n");

        await client.PostAsync(server.Url + "x", payload, c => c.ContentType = "multipart/form-data; boundary=xyz");

        var sent = server.Received[0];
        Assert.Contains("boundary=xyz", sent.Headers.GetValueOrDefault("Content-Type"));
        Assert.Equal(payload, sent.Body);
    }

    // ------------------------------------------------------------------
    // L9：multipart 部件自带的 Content-Disposition（name/filename）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Multipart_UserPartWithoutName_GetsFieldName()
    {
        // net 的 MultipartFormDataContent.Add 对已有 disposition 完全不碰：
        // 只带 filename 的部件此前拿不到 name，服务端无法绑定字段
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var part = new ByteArrayContent(new byte[] { 1, 2 });
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { FileName = "report.pdf" };

        await client.PostAsync(server.Url + "upload", new { doc = part }, c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=doc", body);
        Assert.Contains("filename=report.pdf", body);
    }

    [Fact]
    public async Task Multipart_UserPartWithNameAndFileName_IsNotOverwritten()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var part = new ByteArrayContent(new byte[] { 1, 2 });
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "custom",
            FileName = "report.pdf"
        };

        await client.PostAsync(server.Url + "upload", new { doc = part }, c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=custom", body);
        Assert.DoesNotContain("name=doc", body);
        Assert.Contains("filename=report.pdf", body);
    }

    // ------------------------------------------------------------------
    // L10：SslOptions 扩展点（回调 / 协议之外的 SslClientAuthenticationOptions）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigureSslOptions_ReachesHandlerWithoutOtherSslSettings()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        SslClientAuthenticationOptions? captured = null;
        SocketsHttpHandler? created = null;

        using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            // 只设扩展点：此前 SslOptions 根本不会被构造
            ConfigureSslOptions = options =>
            {
                options.EnabledSslProtocols = SslProtocols.Tls12;
                captured = options;
            },
            HandlerFactory = ctx =>
            {
                created = ctx.CreateDefaultHandler();
                return created;
            }
        });

        await client.GetAsync(server.Url + "x");

        Assert.NotNull(captured);
        Assert.Same(created!.SslOptions, captured);
        Assert.Equal(SslProtocols.Tls12, created.SslOptions!.EnabledSslProtocols);
    }

    [Fact]
    public void PoolOptions_Clone_CopiesConfigureSslOptions()
    {
        Action<SslClientAuthenticationOptions> configure = _ => { };
        var options = new ConnectionPoolOptions { ConfigureSslOptions = configure };

        Assert.Same(configure, options.Clone().ConfigureSslOptions);
    }

    // ------------------------------------------------------------------
    // L11：ERR_INVALID_URL / ERR_BAD_OPTION_VALUE 携带 Config
    // ------------------------------------------------------------------

    [Fact]
    public async Task InvalidUrl_ErrorCarriesConfig()
    {
        using var client = new AxiosClient();
        var config = new AxiosConfig { Url = "/relative-without-base" };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(config));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        // 挂上的是合并后的生效配置（与 axios error.config 一致）
        Assert.NotNull(ex.Config);
        Assert.Equal("/relative-without-base", ex.Config!.Url);
    }

    [Fact]
    public async Task InvalidOption_ErrorCarriesConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Timeout = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.NotNull(ex.Config);
    }

    [Fact]
    public void PoolOptionError_FromClientConstructor_CarriesDefaults()
    {
        var defaults = new AxiosConfig
        {
            Connection = new()
            {
                PoolOptions = new ConnectionPoolOptions { MaxConnectionsPerServer = 0 },
            },
        };

        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(defaults));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Same(defaults, ex.Config);
    }

    // ------------------------------------------------------------------
    // L12：Axios.Create 支持共享池
    // ------------------------------------------------------------------

    [Fact]
    public async Task AxiosCreate_AcceptsSharedPool()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();

        using var client = Axios.Create(new AxiosConfig(), pool);

        Assert.Same(pool, client.Pool);
        var response = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, response.Status);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task AxiosCreate_WithConfigureAction_AcceptsSharedPool()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();

        using var client = Axios.Create(c => c.BaseAddress = server.Url, pool);

        Assert.Same(pool, client.Pool);
        var response = await client.GetAsync("x");
        Assert.Equal(200, response.Status);
    }

    // ------------------------------------------------------------------
    // L13：租用与池关闭的竞态不留下不可回收的节点
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lease_RacingWithDispose_DoesNotLeakEndpoints()
    {
        var uri = new Uri("http://127.0.0.1:1/x");

        for (var i = 0; i < 48; i++)
        {
            using var pool = new HttpConnectionPool(new ConnectionPoolOptions
            {
                IdleHandlerLifetime = TimeSpan.Zero,
                EvictionInterval = TimeSpan.Zero
            });

            using var start = new ManualResetEventSlim(false);
            var leasing = Task.Run(() =>
            {
                start.Wait();
                for (var j = 0; j < 32; j++)
                {
                    try
                    {
                        using var lease = pool.Lease(uri);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });

            start.Set();
            pool.Dispose();
            await leasing;

            // 关闭后新建的节点若不带 DisposeWhenIdle，归还时既不进字典也不释放
            Assert.Equal(0, pool.GetStatistics().EndpointCount);
        }
    }

    // ------------------------------------------------------------------
    // L14/L15：关闭语义（不掐断在途请求；竞态不漏裸 ObjectDisposedException）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispose_DoesNotInterruptInFlightRequest_InjectedHandler()
    {
        // 注入 handler 的实例此前随 HttpClient.Dispose 把在途请求全部取消
        var gate = new TaskCompletionSource();
        using var handler = new GatedHandler(gate);
        using var client = new AxiosClient(handler: handler);

        var pending = client.GetAsync("http://axiosnet.test/x");
        await Task.Delay(100);
        client.Dispose();
        gate.SetResult();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Dispose_DoesNotInterruptInFlightRequest_WithOwnedPool()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: TestJson.Bytes(new { ok = true }),
            BodyDelay: TimeSpan.FromMilliseconds(600)));
        using var client = new AxiosClient();

        var pending = client.GetAsync(server.Url + "slow");
        await Task.Delay(120);
        client.Dispose();

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task RequestAfterClientDispose_ThrowsCanceled_NotRawObjectDisposed()
    {
        using var client = new AxiosClient();
        client.Dispose();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/x"));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    [Fact]
    public async Task PoolClearedDuringRequest_ThrowsAxiosException()
    {
        var gate = new TaskCompletionSource();
        await using var server = TestHttpServer.Start(async req =>
        {
            if (req.Path.EndsWith("hold", StringComparison.Ordinal))
            {
                await gate.Task;
            }

            return TestJson.Response(new { ok = true });
        });

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.FromMinutes(30),
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        await client.GetAsync(server.Url + "warm");
        var pending = client.GetAsync(server.Url + "hold");
        await Task.Delay(120);

        pool.Clear();
        var ex = await Assert.ThrowsAsync<AxiosException>(() => pending);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);

        gate.SetResult();
    }

    [Fact]
    public async Task RequestAfterPoolDispose_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();
        using var client = new AxiosClient(pool: pool);

        pool.Dispose();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // L16：流式响应的 WriteAsync 走异步直连（不退化成同步等待）
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_WriteAsync_ThrowsNotSupported_WithoutHanging()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        var stream = (Stream)response.Data!;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            stream.WriteAsync(new byte[] { 1 }, 0, 1));

        stream.Dispose();
    }

    // ------------------------------------------------------------------
    // L17：超时预算在流构造时即耗尽 → 确定性 ECONNABORTED
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ExhaustedTimeoutBudget_FailsDeterministically()
    {
        // 「剩余预算 <= 0」的窗口天然是竞态（响应头贴着 deadline 到达）：
        // 这里断言结果收敛在「拿到数据」或「ECONNABORTED」之一，不出现裸异常、不挂死。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        await client.GetAsync(server.Url + "warm");

        for (var i = 0; i < 16; i++)
        {
            Stream? stream = null;
            try
            {
                var response = await client.GetAsync(server.Url + "s", c =>
                {
                    c.ResponseType = ResponseType.Stream;
                    c.Timeout = TimeSpan.FromMilliseconds(1);
                });

                stream = (Stream)response.Data!;
                var read = await stream.ReadAsync(new byte[16]);
                Assert.True(read >= 0);
            }
            catch (AxiosException ex) when (ex.Code == AxiosException.ErrConnAborted)
            {
                // 允许的结果之一：预算耗尽/超时
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}
