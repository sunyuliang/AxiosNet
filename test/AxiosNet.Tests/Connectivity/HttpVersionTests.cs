
using AxiosNet.Pooling;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// HTTP 版本：HTTP/2 默认与降级、HTTP/3 探测与精确策略、池选项克隆。
/// </summary>
public sealed class HttpVersionTests
{
    // ---------------------------------------------------------------
    // HttpVersion：请求行版本由 config 决定
    // （「默认 HTTP/2、明文自动降级 1.1」见 HttpVersionNegotiationTests）
    // ---------------------------------------------------------------

    [Fact]
    public async Task HttpVersion_CanBeDowngradedTo10()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.Connection.HttpVersion = new Version(1, 0));

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.0", server.Received[0].Version);
    }

    // ------------------------------------------------------------------
    // 本地能力探测
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_IsSelfConsistent()
    {
        // 两个属性必须互为反面：不支持时必须给得出原因（错误信息直接引用它），支持时不该留原因。
        Assert.Equal(Http3Support.IsSupported, Http3Support.UnsupportedReason is null);

        if (!Http3Support.IsSupported)
        {
            Assert.False(string.IsNullOrWhiteSpace(Http3Support.UnsupportedReason));
        }
    }

    [Fact]
    public void Probe_MatchesRuntimeQuicCapability()
    {
        // 独立复核：运行时自己的 QUIC 结论（QuicConnection.IsSupported）与本库探测必须一致。
        // 这条断言的价值在于「本库没有自己发明一套判断」，而不是重复实现一遍。
        var quic = Type.GetType("System.Net.Quic.QuicConnection, System.Net.Quic", throwOnError: false);
        var isSupportedProperty = quic?.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);

        // .NET 6 的 System.Net.Quic 没有这个属性（.NET 7 才加）：本地没有可交叉验证的第二来源
        if (isSupportedProperty?.GetValue(null) is not bool runtimeQuicSupported)
        {
            return;
        }

        // 开关被显式关掉时两边本来就不同：QUIC 可用，但 h3 被禁用
        if (AppContext.TryGetSwitch(Http3SwitchName, out var switchEnabled) && !switchEnabled)
        {
            return;
        }

        Assert.Equal(runtimeQuicSupported, Http3Support.IsSupported);
    }

    // ------------------------------------------------------------------
    // 默认策略：h3 只是「优先」，永远不该在客户端被拦下
    // ------------------------------------------------------------------

    [Fact]
    public async Task OrLowerPolicy_PassesHttp3ThroughToHandler()
    {
        // 默认策略 OrLower = 「能 h3 就 h3、不能就降级」：降级是它的正常路径，
        // 所以即使本机没有 QUIC，版本与策略也要原样交给 BCL（由它在握手阶段决定落到哪个版本）。
        var handler = new RecordingHandler();
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync(
            "https://example.test/ping",
            c => c.Connection.HttpVersion = HttpVersion.Version30);

        Assert.Equal(200, response.Status);
        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version30, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
    }

    // ------------------------------------------------------------------
    // 强制策略：环境不可能做到就立刻失败（一个字节都不发）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExactPolicy_OnCleartext_FailsBeforeAnyConnection()
    {
        // h3 只能跑在 QUIC（TLS 1.3 over UDP）上，明文目标上「必须 h3」是不可能成立的配置。
        // 关键是失败得早：不能先建 TCP 连接、等一次失败再报网络错误 —— 那样调用方会去查网络。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("HTTP/3", ex.Message);
        Assert.Equal(0, server.TotalConnections);
    }

    [Fact]
    public async Task OrHigherPolicy_OnCleartext_FailsBeforeAnyConnection()
    {
        // OrHigher 与 Exact 同义（不存在比 3.0 更高的版本），不能因为它字面上像「放宽」就漏判。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Equal(0, server.TotalConnections);
    }

    [Fact]
    public async Task ExactPolicy_WhenRuntimeLacksQuic_FailsBeforeAnyConnection()
    {
        // 本机没有 QUIC 时「必须 h3」同样是不可能成立的配置：与其让 BCL 报 ERR_NETWORK（调用方会去查网络
        // 或服务端），不如在发请求前说清楚缺什么（开关 / MsQuic）。
        if (Http3Support.IsSupported)
        {
            return;
        }

        var handler = new RecordingHandler();
        using var client = new AxiosClient(handler: handler);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("https://example.test/ping", c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains(Http3Support.UnsupportedReason!, ex.Message);
        Assert.Equal(0, handler.SentCount);
    }

    [Fact]
    public async Task ExactPolicy_OnHttp11_IsUnaffected()
    {
        // 前置校验只看 3.x：显式要求 1.1 + Exact 的老场景（老网关 / NTLM）不能被误伤。
        var handler = new RecordingHandler();
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("https://example.test/ping", c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version11;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(HttpVersion.Version11, Assert.Single(handler.Sent).Version);
    }

    // ------------------------------------------------------------------
    // 重试：h3 的版本与策略要跟着每一次尝试走
    // ------------------------------------------------------------------

    [Fact]
    public async Task Retry_KeepsHttp3VersionAndPolicy()
    {
        var handler = new RecordingHandler
        {
            Responder = attempt => attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
        };

        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("https://example.test/ping", c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Retry.Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero };
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, handler.Sent.Count);
        Assert.All(handler.Sent, sent =>
        {
            // 只搬 Version 不搬策略会静默退回 OrLower，只搬策略不搬 Version 会静默变成 2.0
            Assert.Equal(HttpVersion.Version30, sent.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
        });
    }

    // ------------------------------------------------------------------
    // 出站请求携带的版本与策略
    // ------------------------------------------------------------------

    [Fact]
    public async Task Default_RequestsHttp2()
    {
        // 默认 2.0：h2 已是公网流量主力，默认停在 1.1 等于让绝大多数调用方白白丢掉多路复用。
        var handler = new CapturingHandler();
        using var client = new AxiosClient(handler: handler);

        await client.GetAsync("http://example.test/ping");

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version20, sent.Version);

        // 策略必须显式写死 OrLower：「服务端不支持就降级」是库的契约。
        // 这条断言是默认 2.0 的前提 —— 一旦策略变成 Exact，默认 2.0 就会让所有只认 1.x 的服务直接失败。
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
    }

    [Fact]
    public async Task Config_CanForceHttp11()
    {
        var handler = new CapturingHandler();
        using var client = new AxiosClient(handler: handler);

        // 退回 1.1 也只需要一行：老网关 / NTLM 这类连接级认证 / 必须用连接级头的场景
        await client.GetAsync("http://example.test/ping", c => c.Connection.HttpVersion = HttpVersion.Version11);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version11, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
    }

    [Fact]
    public async Task Config_CanRequireExactVersion()
    {
        var handler = new CapturingHandler();
        using var client = new AxiosClient(handler: handler);

        await client.GetAsync("http://example.test/ping", c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version20;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        });

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version20, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, sent.Policy);
    }

    [Fact]
    public async Task PerRequestVersion_OverridesInstanceDefault()
    {
        // 实例上退回 1.1（比如整个客户端面向一个老网关），单次请求仍可单独要 h2
        var handler = new CapturingHandler();
        using var client = new AxiosClient(
            new AxiosConfig { Connection = { HttpVersion = HttpVersion.Version11 } },
            handler: handler);

        await client.GetAsync("http://example.test/ping");
        await client.GetAsync("http://example.test/ping", c => c.Connection.HttpVersion = HttpVersion.Version20);

        Assert.Equal(
            new[] { HttpVersion.Version11, HttpVersion.Version20 },
            handler.Sent.Select(s => s.Version));
    }

    [Fact]
    public async Task PerRequestPolicy_OverridesInstanceDefault()
    {
        var handler = new CapturingHandler();
        using var client = new AxiosClient(
            new AxiosConfig { Connection = { VersionPolicy = HttpVersionPolicy.RequestVersionExact } },
            handler: handler);

        await client.GetAsync("http://example.test/ping");
        await client.GetAsync("http://example.test/ping", c => c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower);

        Assert.Equal(
            new[] { HttpVersionPolicy.RequestVersionExact, HttpVersionPolicy.RequestVersionOrLower },
            handler.Sent.Select(s => s.Policy));
    }

    // ------------------------------------------------------------------
    // 线上行为：明文连接
    // ------------------------------------------------------------------

    [Fact]
    public async Task DefaultOnCleartext_FallsBackToHttp11()
    {
        // 默认是 2.0，但明文连接没有 ALPN 可协商，OrLower 必须落到 1.1 ——
        // 这正是「默认 2.0 只影响 https」的证据：http:// 的线上行为与之前完全一致。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.1", server.Received[0].Version);
    }

    [Fact]
    public async Task ExplicitHttp11OnCleartext_WithExactPolicy_Succeeds()
    {
        // 显式退回 1.1 时，即使策略是 Exact 也不该被误解成「明文跑 h2」：
        // 目标是 1.1、对端也只说 1.1，就该正常成功，不能因为 Exact 就去发 h2c 序言。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version11;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        });

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.1", server.Received[0].Version);
    }

    [Fact]
    public async Task ExactPolicy_OnCleartext_SendsHttp2ConnectionPreface()
    {
        // RequestVersionExact 是「明文上也用 h2」的唯一开关（h2c 先验知识）：
        // 服务端只要能收到 HTTP/2 的连接序言，就证明出站请求真的按 h2 起手，而不只是一个属性值。
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var preface = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                var buffer = new byte[24];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset));
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                preface.TrySetResult(buffer[..offset]);
            }
            catch
            {
                preface.TrySetResult(Array.Empty<byte>());
            }
        });

        try
        {
            using var client = new AxiosClient();

            // 本用例只关心「客户端怎么起手」：服务端读完序言就断开，客户端必然报错
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(
                $"http://127.0.0.1:{port}/",
                c =>
                {
                    c.Connection.HttpVersion = HttpVersion.Version20;
                    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                }));

            var received = await preface.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Encoding.ASCII.GetString(received));
        }
        finally
        {
            listener.Stop();
        }
    }

    // ------------------------------------------------------------------
    // 线上行为：TLS / ALPN
    // ------------------------------------------------------------------

    [Fact]
    public async Task TlsServerWithOnlyHttp11_FallsBackToHttp11()
    {
        // 生产路径（不设任何版本配置，走默认 2.0）：服务端 ALPN 只给 http/1.1 ——
        // 必须在同一次握手里谈成 1.1 并成功，而不是抛协议错误。
        // 这就是「默认 2.0 不会让老服务失败」的实证。
        await using var server = TlsProbeServer.Start(SslApplicationProtocol.Http11);
        using var client = new AxiosClient(poolOptions: TrustAnyCertificate());

        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        var first = Encoding.ASCII.GetString(await server.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("GET / HTTP/1.1", first);
    }

    [Fact]
    public async Task TlsServerWithH2Alpn_SpeaksHttp2()
    {
        // 反向确认：同样是默认配置，服务端 ALPN 只给 h2 时客户端确实按 h2 起手（连接序言），
        // 说明上面的降级是「协商不上才降」，而不是「永远只发 1.1」—— 默认值真的生效了。
        await using var server = TlsProbeServer.Start(SslApplicationProtocol.Http2);
        using var client = new AxiosClient(poolOptions: TrustAnyCertificate());

        // 本用例的服务端只读序言就断开，客户端必然报错
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(server.Url));

        var first = Encoding.ASCII.GetString(await server.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", first);
    }

    // ------------------------------------------------------------------
    // 协商结果对调用方可见（降级是静默的，这是唯一的确认入口）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Response_ReportsNegotiatedVersion()
    {
        // 让桩 Handler 把响应标成 2.0，而请求也是默认的 2.0 —— 值必须取自**响应**；
        // 为了有判别力，下面的用例让两者不同（请求 2.0 / 响应 1.1）。
        var handler = new CapturingHandler { ResponseVersion = HttpVersion.Version20 };
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("http://example.test/ping");

        Assert.Equal(HttpVersion.Version20, response.ProtocolVersion);
    }

    [Fact]
    public async Task Response_ReportsDowngradedVersion()
    {
        // 请求要 2.0、响应标 1.1：模拟真实降级后的结果，调用方必须能看到「实际是 1.1」。
        var handler = new CapturingHandler { ResponseVersion = HttpVersion.Version11 };
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("http://example.test/ping", c => c.Connection.HttpVersion = HttpVersion.Version20);

        Assert.Equal(HttpVersion.Version20, Assert.Single(handler.Sent).Version);
        Assert.Equal(HttpVersion.Version11, response.ProtocolVersion);
    }

    // ------------------------------------------------------------------
    // HTTP/3：版本原样透传（本库不做 Alt-Svc 发现）；「环境不可能」的严格策略由
    // Http3SupportTests 覆盖
    // ------------------------------------------------------------------

    [Fact]
    public async Task Http3OnCleartext_FallsBackToHttp11()
    {
        // h3 只能跑在 QUIC（TLS 1.3 over UDP）上，明文 http:// 上不存在 h3：
        // 传 3.0 + 默认 OrLower 必须落到 1.1 而不是抛错，否则「显式要求 h3」会让明文目标全失败。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.Connection.HttpVersion = HttpVersion.Version30);

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.1", server.Received[0].Version);
        Assert.Equal(HttpVersion.Version11, response.ProtocolVersion);
    }

    [Fact]
    public async Task Http3WithExactPolicy_OnCleartext_FailsBeforeSending()
    {
        // Exact + 3.0 = 「必须是 h3」：明文目标上不可能成立，属于配置错误而不是网络故障，
        // 所以要在发请求之前就报库的错误码（一个 TCP 连接都不建），不能把 BCL 的 HttpRequestException
        // 漏给调用方，也不能让调用方拿着 ERR_NETWORK 去查网络。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Equal(0, server.TotalConnections);
    }

    // ------------------------------------------------------------------
    // 重试：复制请求不能丢版本策略
    // ------------------------------------------------------------------

    [Fact]
    public async Task Retry_KeepsVersionPolicyOnEveryAttempt()
    {
        var handler = new CapturingHandler
        {
            Responder = attempt => attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
        };

        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("http://example.test/ping", c =>
        {
            // 两项都取非默认值（默认是 2.0 + OrLower）：只搬 Version 不搬策略会静默退回 OrLower，
            // 只搬策略不搬 Version 会静默变成 2.0 —— 两个字段都必须过一遍 CloneRequest。
            c.Connection.HttpVersion = HttpVersion.Version11;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            c.Retry.Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero };
        });

        Assert.Equal(200, response.Status);

        Assert.Equal(2, handler.Sent.Count);
        Assert.All(handler.Sent, sent =>
        {
            Assert.Equal(HttpVersion.Version11, sent.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, sent.Policy);
        });
    }

    // ------------------------------------------------------------------
    // 连接池：h2 多连接
    // ------------------------------------------------------------------

    [Fact]
    public async Task PoolOptions_EnableMultipleHttp2Connections_DefaultsToTrue()
    {
        SocketsHttpHandler? created = null;

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            EvictionInterval = TimeSpan.Zero,
            HandlerFactory = context =>
            {
                var handler = context.CreateDefaultHandler();
                created = handler;
                return handler;
            }
        });

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(pool: pool);
        await client.GetAsync(server.Url);

        Assert.NotNull(created);
        Assert.True(created!.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void PoolOptions_EnableMultipleHttp2Connections_IsCloned()
    {
        var options = new ConnectionPoolOptions { EnableMultipleHttp2Connections = false };

        Assert.False(options.Clone().EnableMultipleHttp2Connections);
        Assert.True(new ConnectionPoolOptions().EnableMultipleHttp2Connections);
    }

    [Fact]
    public async Task PoolOptions_EnableMultipleHttp2Connections_CanBeDisabled()
    {
        SocketsHttpHandler? created = null;

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            EvictionInterval = TimeSpan.Zero,
            EnableMultipleHttp2Connections = false,
            HandlerFactory = context =>
            {
                var handler = context.CreateDefaultHandler();
                created = handler;
                return handler;
            }
        });

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(pool: pool);
        await client.GetAsync(server.Url);

        Assert.False(created!.EnableMultipleHttp2Connections);
    }

    /// <summary>BCL 的 HTTP/3 开关名：测试里独立写一份字符串，避免「拿实现去验证实现」。</summary>
    private const string Http3SwitchName = "System.Net.SocketsHttpHandler.Http3Support";

    /// <summary>离线桩 Handler：记录每次请求的版本与策略，绝不建连接。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Version Version, HttpVersionPolicy Policy)> _sent = new();

        /// <summary>第 N 次（从 1 开始）调用应返回的状态码，默认 200。</summary>
        public Func<int, HttpStatusCode>? Responder { get; set; }

        public int SentCount
        {
            get
            {
                lock (_sent)
                {
                    return _sent.Count;
                }
            }
        }

        public IReadOnlyList<(Version Version, HttpVersionPolicy Policy)> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int attempt;
            lock (_sent)
            {
                _sent.Add((request.Version, request.VersionPolicy));
                attempt = _sent.Count;
            }

            var response = new HttpResponseMessage(Responder?.Invoke(attempt) ?? HttpStatusCode.OK)
            {
                Version = request.Version,
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>完全离线的桩 Handler：记录每条请求的版本与策略，按序号决定状态码。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<(Version Version, HttpVersionPolicy Policy)> _sent = new();

        /// <summary>第 N 次（从 1 开始）调用应返回的状态码，默认 200。</summary>
        public Func<int, HttpStatusCode>? Responder { get; set; }

        /// <summary>响应上标注的协议版本（模拟「服务端实际用了哪个版本」），默认 1.1。</summary>
        public Version ResponseVersion { get; set; } = HttpVersion.Version11;

        public IReadOnlyList<(Version Version, HttpVersionPolicy Policy)> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int attempt;
            lock (_sent)
            {
                _sent.Add((request.Version, request.VersionPolicy));
                attempt = _sent.Count;
            }

            var response = new HttpResponseMessage(Responder?.Invoke(attempt) ?? HttpStatusCode.OK)
            {
                Version = ResponseVersion,
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    private static ConnectionPoolOptions TrustAnyCertificate() => new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };

    /// <summary>
    /// 只支持指定 ALPN 协议的极简 TLS 服务器，记录客户端发来的第一批字节。
    /// <para>
    /// h1 场景按 HTTP/1.1 回 200（验证降级后请求照常成功）；h2 场景读到 24 字节连接序言就断开
    /// —— 本用例只关心「协商成了哪个版本、客户端怎么起手」，不实现完整 h2 服务端。
    /// </para>
    /// </summary>
    private sealed class TlsProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();

        private TlsProbeServer(SslApplicationProtocol protocol, X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(() => AcceptLoopAsync(protocol));
        }

        public string Url { get; }

        /// <summary>客户端发来的第一批字节（请求行，或 h2 连接序言）。</summary>
        public TaskCompletionSource<byte[]> FirstBytes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TlsProbeServer Start(SslApplicationProtocol protocol)
            => new(protocol, CreateSelfSignedCertificate());

        private async Task AcceptLoopAsync(SslApplicationProtocol protocol)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client, protocol));
            }
        }

        private async Task HandleAsync(TcpClient client, SslApplicationProtocol protocol)
        {
            using (client)
            {
                try
                {
                    var ssl = new SslStream(client.GetStream());
                    await using (ssl)
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            ApplicationProtocols = new List<SslApplicationProtocol> { protocol }
                        });

                        var isHttp2 = protocol == SslApplicationProtocol.Http2;
                        var buffer = new byte[64];
                        var offset = 0;
                        while (offset < buffer.Length)
                        {
                            var read = await ssl.ReadAsync(buffer.AsMemory(offset));
                            if (read == 0)
                            {
                                break;
                            }

                            offset += read;

                            // h2：读满 24 字节序言即可；h1：读到请求行的 CRLF 为止
                            if (isHttp2 ? offset >= 24 : buffer.AsSpan(0, offset).IndexOf("\r\n"u8) >= 0)
                            {
                                break;
                            }
                        }

                        FirstBytes.TrySetResult(buffer[..offset]);

                        if (!isHttp2)
                        {
                            var response = "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nContent-Type: application/json\r\n\r\n{\"ok\":true}";
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes(response));
                            await ssl.FlushAsync();
                        }
                    }
                }
                catch
                {
                    // 协商失败或客户端提前断开：用例只关心成功路径上观察到的东西
                }
            }
        }

        private static X509Certificate2 CreateSelfSignedCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddIpAddress(IPAddress.Loopback);
            alternativeNames.AddDnsName("localhost");
            request.CertificateExtensions.Add(alternativeNames.Build());

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Windows 的 SslStream 不接受只有内存私钥的证书：导出成 PFX 再导入一次拿到可用的密钥句柄
            return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
                _listener.Stop();
                _certificate.Dispose();
            }
            catch
            {
                // ignore
            }

            return ValueTask.CompletedTask;
        }
    }
}