using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// HTTP 版本协商：默认 1.1（与 HttpClient 一致）、显式开启 HTTP/2 后服务端不支持时自动降级、
/// 按请求覆盖版本与策略、重试复制请求时不丢策略、以及池对 h2 多连接的支持。
/// <para>
/// 「降级」本身是 BCL 的行为（TLS 上走 ALPN，明文上没有 ALPN 可协商），库的职责是把
/// <c>Version</c> / <c>VersionPolicy</c> 正确写进出站请求。因此这里的用例分两层：
/// 一层断言出站请求携带的版本与策略，一层用真实 socket 观察线上行为（TLS/ALPN 协商、明文降级、h2c 先验知识）。
/// </para>
/// </summary>
public sealed class HttpVersionNegotiationTests
{
    /// <summary>完全离线的桩 Handler：记录每条请求的版本与策略，按序号决定状态码。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<(Version Version, HttpVersionPolicy Policy)> _sent = new();

        /// <summary>第 N 次（从 1 开始）调用应返回的状态码，默认 200。</summary>
        public Func<int, HttpStatusCode>? Responder { get; set; }

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
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    // ------------------------------------------------------------------
    // 出站请求携带的版本与策略
    // ------------------------------------------------------------------

    [Fact]
    public async Task Default_RequestsHttp11()
    {
        // 默认与 HttpClient 一致：升级本库不改变线上协议，HTTP/2 由调用方显式开启。
        var handler = new CapturingHandler();
        using var client = new AxiosClient(handler: handler);

        await client.GetAsync("http://example.test/ping");

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version11, sent.Version);

        // 策略仍然显式写死 OrLower：开了 h2 之后「服务端不支持就降级」是库的契约，
        // 不能依赖 BCL 的默认策略（哪天改成 Exact，这条承诺就静默失效了）。
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.Policy);
    }

    [Fact]
    public async Task Config_CanEnableHttp2()
    {
        var handler = new CapturingHandler();
        using var client = new AxiosClient(handler: handler);

        // 开启 h2 只需要一行：策略默认 OrLower，服务端不支持会自动降级
        await client.GetAsync("http://example.test/ping", c => c.Connection.HttpVersion = HttpVersion.Version20);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpVersion.Version20, sent.Version);
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
        // 实例上开了 h2，单次请求仍可退回 1.1（老网关 / NTLM 这类例外调用）
        var handler = new CapturingHandler();
        using var client = new AxiosClient(
            new AxiosConfig { Connection = { HttpVersion = HttpVersion.Version20 } },
            handler: handler);

        await client.GetAsync("http://example.test/ping");
        await client.GetAsync("http://example.test/ping", c => c.Connection.HttpVersion = HttpVersion.Version11);

        Assert.Equal(
            new[] { HttpVersion.Version20, HttpVersion.Version11 },
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
    public async Task Default_OnCleartext_SendsHttp11()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        Assert.Equal("HTTP/1.1", server.Received[0].Version);
    }

    [Fact]
    public async Task Http2OnCleartext_FallsBackToHttp11()
    {
        // 显式开了 h2 但目标只有明文 HTTP：没有 ALPN 可协商，OrLower 必须落到 1.1，
        // 不能拿 h2c 先验知识去撞只认 1.x 的服务端（那样每个老服务都会连不上）。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.Connection.HttpVersion = HttpVersion.Version20);

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
        // 生产路径：显式开了 h2，但服务端 ALPN 只给 http/1.1 —— 必须自动降级并在 1.1 上成功，
        // 而不是抛协议错误（这就是「开 h2 不影响老服务」的实证）。
        await using var server = TlsProbeServer.Start(SslApplicationProtocol.Http11);
        using var client = new AxiosClient(Http2Config(), poolOptions: TrustAnyCertificate());

        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        var first = Encoding.ASCII.GetString(await server.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("GET / HTTP/1.1", first);
    }

    [Fact]
    public async Task TlsServerWithH2Alpn_SpeaksHttp2()
    {
        // 反向确认：服务端 ALPN 只给 h2 时，客户端确实按 h2 起手（连接序言），
        // 说明上面的降级是「协商不上才降」，而不是「永远只发 1.1」。
        await using var server = TlsProbeServer.Start(SslApplicationProtocol.Http2);
        using var client = new AxiosClient(Http2Config(), poolOptions: TrustAnyCertificate());

        // 本用例的服务端只读序言就断开，客户端必然报错
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(server.Url));

        var first = Encoding.ASCII.GetString(await server.FirstBytes.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", first);
    }

    /// <summary>开启 HTTP/2 的实例配置（策略保持默认的「不支持就降级」）。</summary>
    private static AxiosConfig Http2Config() => new()
    {
        Connection = { HttpVersion = HttpVersion.Version20 }
    };

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

            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            // Windows 的 SslStream 不接受只有内存私钥的证书：导出成 PFX 再导入一次拿到可用的密钥句柄
            return new X509Certificate2(
                certificate.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.Exportable);
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
            c.Connection.HttpVersion = HttpVersion.Version20;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            c.Retry.Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero };
        });

        Assert.Equal(200, response.Status);

        // 第 2 次是 CloneRequest 出来的新实例：只搬 Version 不搬策略，会静默退回 OrLower
        Assert.Equal(2, handler.Sent.Count);
        Assert.All(handler.Sent, sent =>
        {
            Assert.Equal(HttpVersion.Version20, sent.Version);
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
}
