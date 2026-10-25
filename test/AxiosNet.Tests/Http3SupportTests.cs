using System.Net;
using System.Reflection;
using System.Text;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// HTTP/3 支持：本地能力探测（<see cref="Http3Support"/>）与「明确要求 h3 但环境不可能」时的前置失败。
/// <para>
/// 默认仍是 HTTP/2 —— 那条回归护栏在 <see cref="HttpVersionNegotiationTests"/>。真正跑通 h3 需要 QUIC
/// （MsQuic / libmsquic），开发机与 CI 不一定具备，所以这里的断言只落在两件与机器无关的事上：
/// ① 探测结论必须与运行时事实一致；② 环境不可能时必须在发请求<b>之前</b>失败，而不是等一次连接超时。
/// </para>
/// </summary>
public sealed class Http3SupportTests
{
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
}
