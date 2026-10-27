using System.Net;
using System.Net.Http;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-23 全库审查「中/低」清单的逐条动态验证 + 修复回归。
/// <para>
/// 每条都先以「期望行为」写成用例：改动前失败 = 动态复现了缺陷，改动后通过 = 修复生效。
/// 验证后判定为「非缺陷 / 已由既有实现覆盖」的项不在此处提交断言，另见交付说明：
/// M1（流式响应 Data 被拦截器换掉时的租约泄漏：用例 M1_... 已通过，AxiosClient 早就在
///     拦截器链返回后按引用比对释放了流包装，无泄漏）、
/// L5（Clone/Exclusive 隔离窗口，纯理论竞态，无动态复现路径）、
/// L6（InvalidUrl 消息内联 URL：消息是唯一诊断载体且已截断，属设计取舍）、
/// L7（ReflectionCache 无上限：被引用类型数天然有界）、
/// L8（ClearPendingRemoval 只摘一条：静态头数组同名删除项必相邻且唯一，构造不出重复）、
/// L9（EvictIdle 计数漏加：现实现已 evicted++，且 Audit20261022GlobalAuditFixTests
///     EnsureCapacity_EvictsLeastRecentlyUsedIdleEndpoint 已覆盖该症状）、
/// L11（multipart 非 ASCII 字段名 RFC 2047：需真实服务端解析才能判定）、
/// L14（TimeoutStream.Link 的 CTS 恒为 null：死值，无行为影响，改造反而动到流清理路径）。
/// </para>
/// </summary>
public sealed class Audit20261023GlobalFixTests
{
    // ===============================================================
    // M1：流式响应被响应拦截器换成非 Stream 的 Data 时，连接租约必须归还
    // ===============================================================

    [Fact]
    public async Task M1_StreamResponse_InterceptorReplacingData_LeasesReturned()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"n\":1}\n"),
            ContentType: "application/x-ndjson"));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });
        using var client = new AxiosClient(pool: pool);

        client.Interceptors.Response.Use(response =>
        {
            response.Data = null;
            return response;
        });

        var response = await client.GetAsync<Stream>(server.Url + "stream");

        Assert.Equal(200, response.Status);
        Assert.Null(response.Data);

        // 拦截器丢掉了流式正文：包装流（持有租约 + HttpResponseMessage）必须在这里被释放
        Assert.Equal(0, ActiveLeases(pool));
    }

    // ===============================================================
    // M2：ApiClient 默认值不能静默覆盖底层 AxiosClient 已配置的 StreamIdleTimeout
    // ===============================================================

    [Fact]
    public async Task M2_ApiClient_KeepsAxiosClientStreamIdleTimeoutDefault()
    {
        var defaults = new AxiosConfig
        {
            BaseAddress = "http://stream.test/",
            Limits = new()
            {
                StreamIdleTimeout = TimeSpan.FromSeconds(20),
            },
        };

        using var api = CreateStreamApiClient(defaults);

        var response = await api.RequestStreamAsync(new IdleStreamRequest());

        // 底层客户端显式配了 20s，ApiClient 没显式设置时不得把它压成「不限时」
        Assert.Equal(TimeSpan.FromSeconds(20), response.Config!.Limits.StreamIdleTimeout);
        await DrainAsync(response.Data);
    }

    [Fact]
    public async Task M2_ApiClient_ExplicitValueStillWins()
    {
        var defaults = new AxiosConfig
        {
            BaseAddress = "http://stream.test/",
            Limits = new()
            {
                StreamIdleTimeout = TimeSpan.FromSeconds(20),
            },
        };

        using var api = CreateStreamApiClient(defaults);
        api.StreamIdleTimeout = TimeSpan.FromSeconds(3);

        var response = await api.RequestStreamAsync(new IdleStreamRequest());

        Assert.Equal(TimeSpan.FromSeconds(3), response.Config!.Limits.StreamIdleTimeout);
        await DrainAsync(response.Data);
    }

    [Fact]
    public async Task M2_ApiClient_DefaultStaysUnlimited_WhenClientHasNoIdleDefault()
    {
        // 底层没配 StreamIdleTimeout：ApiClient 的文档默认（Zero = 正文不限时）必须保持
        using var api = CreateStreamApiClient(new AxiosConfig
        {
            BaseAddress = "http://stream.test/",
            Timeout = TimeSpan.FromMilliseconds(120)
        });

        var response = await api.RequestStreamAsync(new IdleStreamRequest());

        Assert.Equal(TimeSpan.Zero, response.Config!.Limits.StreamIdleTimeout);
        await DrainAsync(response.Data);
    }

    [Fact]
    public async Task M2_ApiClient_KeepsExplicitNullDefault_SharingTimeoutBudget()
    {
        // 显式设成 null 是有效意图「正文与响应头共享 Timeout 预算」，不能因为「值等于未设置」
        // 就被 ApiClient 的默认 Zero（正文不限时）顶掉。
        var defaults = new AxiosConfig
        {
            BaseAddress = "http://stream.test/",
            Limits = new()
            {
                StreamIdleTimeout = null,
            },
        };

        using var api = CreateStreamApiClient(defaults);

        var response = await api.RequestStreamAsync(new IdleStreamRequest());

        Assert.Null(response.Config!.Limits.StreamIdleTimeout);
        await DrainAsync(response.Data);
    }

    // ===============================================================
    // M3：Headers 里声明的 Content-Type charset 与实际正文编码矛盾时不能静默发出
    // ===============================================================

    [Fact]
    public async Task M3_HeadersContentTypeCharset_ConflictsWithBodyEncoding_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 正文按默认 UTF-8 编码，头却声明 iso-8859-1：服务端按头解出乱码且没有任何报错
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "x",
            "héllo",
            c => c.Headers.Set("Content-Type", "text/plain; charset=iso-8859-1")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task M3_HeadersContentTypeCharset_MatchingBodyEncoding_IsAllowed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "x",
            "héllo",
            c => c.Headers.Set("Content-Type", "text/plain; charset=utf-8"));

        Assert.Equal("héllo", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }

    // ===============================================================
    // M4：ConnectTimeout 超过 SocketsHttpHandler 上限时必须走统一错误模型
    // ===============================================================

    [Fact]
    public void M4_ConnectTimeout_BeyondHandlerLimit_ThrowsAxiosException()
    {
        var limit = TimeSpan.FromMilliseconds(int.MaxValue);

        // 前提核实：池侧据以判定的上限必须与底层 Handler 的真实上限一致 —— 含端点可用、+1 毫秒即抛。
        // 上限是 BCL 常量，故当场用探针取证（而不是把常量抄进断言；将来 BCL 放宽会立刻暴露）。
        using (var probe = new SocketsHttpHandler())
        {
            probe.ConnectTimeout = limit;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => probe.ConnectTimeout = limit + TimeSpan.FromMilliseconds(1));
        }

        // 端点本身是合法值：池不能比 BCL 更严（多拦 1 毫秒 = 让原本可用的配置在升级后报错）
        using (var accepted = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" },
            poolOptions: new ConnectionPoolOptions { ConnectTimeout = limit }))
        {
            Assert.Equal(limit, accepted.Pool!.Options.ConnectTimeout);
        }

        // 越界值必须在构造期就以统一错误模型拦下，而不是等首次租用连接时漏出裸 BCL 异常（那时连 Config 都没有）
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" },
            poolOptions: new ConnectionPoolOptions { ConnectTimeout = TimeSpan.FromDays(30) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("ConnectTimeout", ex.Message);
    }

    // ===============================================================
    // L10：HandlerFactory 拿到的 Options 不得是池的实时可变本体
    // ===============================================================

    [Fact]
    public void L10_HandlerFactory_DoesNotMutatePoolLiveOptions()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            EvictionInterval = TimeSpan.Zero,
            HandlerFactory = ctx =>
            {
                ctx.Options.MaxConnectionsPerServer = 7;
                return ctx.CreateDefaultHandler();
            }
        });

        using (pool.Lease(new Uri("http://127.0.0.1:9301/x")))
        {
        }

        Assert.Equal(100, pool.Options.MaxConnectionsPerServer);
    }

    // ===============================================================
    // L12：JSON 字节入口要和处理文本路径一样认 UTF-16 / UTF-32 BOM
    // ===============================================================

    [Fact]
    public async Task L12_JsonByteEntry_Utf16Bom_DecodesLikeTextPath()
    {
        var json = "{\"name\":\"héllo\",\"age\":3}";
        var body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(json))
            .ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body, ContentType: "application/octet-stream"));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var response = await client.GetAsync<Person>("x");

        Assert.Equal("héllo", response.Data!.Name);
        Assert.Equal(3, response.Data.Age);
    }

    // ===============================================================
    // L13：ContentType 的 charset 判重要容忍 "charset = utf-8" 这种空格写法
    // ===============================================================

    [Fact]
    public async Task L13_ContentTypeCharsetWithSpaces_DoesNotDuplicateCharset()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync("x", "hi", c =>
        {
            c.ContentType = "text/plain; charset = utf-8";
            c.Serialization.RequestEncoding = Encoding.UTF8;
        });

        var contentType = Assert.Single(server.Received).Headers["content-type"];
        Assert.Equal(1, CountOccurrences(contentType, "charset"));
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    [Get("stream")]
    private sealed class IdleStreamRequest : IApiRequest<FrameItem>
    {
    }

    private sealed record FrameItem(int N);

    private static ApiClient CreateStreamApiClient(AxiosConfig defaults)
    {
        var handler = new FixedBodyHandler(Encoding.UTF8.GetBytes("{\"n\":1}\n"));
        return new ApiClient(new AxiosClient(defaults, handler: handler, disposeHandler: true), ownsAxiosClient: true);
    }

    private static async Task DrainAsync<T>(IAsyncEnumerable<T>? source)
    {
        if (source is null)
        {
            return;
        }

        await foreach (var _ in source.ConfigureAwait(false))
        {
        }
    }

    private static int ActiveLeases(HttpConnectionPool pool)
        => pool.GetStatistics().Endpoints.Sum(e => e.ActiveLeases);

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    /// <summary>每次都返回同一段 NDJSON 正文的处理器。</summary>
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FixedBodyHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(_body))
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return Task.FromResult(response);
        }
    }
}
