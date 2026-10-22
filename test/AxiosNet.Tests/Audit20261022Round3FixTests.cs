using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 第三轮全局排查修复的回归用例（TDD：每条先写出期望行为并确认红灯，再改实现）。
/// <para>
/// 三类缺陷：
/// <list type="number">
/// <item><b>fail-fast 缺口</b>：快捷方法的 <c>url</c> 为 null / 空白时被静默解释成「请求 baseAddress」，
/// 一次「变量忘赋值」变成向根路径发出的意外请求（POST 可能带副作用）。</item>
/// <item><b>错误模型缺口</b>：上传 / 下载进度回调、自定义 <c>paramsSerializer</c> 抛出的异常都以裸类型
/// 漏给调用方（上传侧还被 HttpClient 包成 <c>HttpRequestException</c> → 误报 <c>ERR_NETWORK</c>），
/// 与「错误统一 <see cref="AxiosException"/>」的契约冲突。</item>
/// <item><b>回放语义错误</b>：<c>response.Config</c> 回放到另一个实例时，上一实例的 <c>BaseAddress</c>
/// 覆盖了目标实例的 —— 请求静默发到旧服务端。</item>
/// </list>
/// </para>
/// </summary>
public sealed class Audit20261022Round3FixTests
{
    // ------------------------------------------------------------------
    // 1) 快捷方法 url 的 fail-fast
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullUrl_ReportsInvalidUrlBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 写错变量（null）不该被静默解释成「请求 baseAddress」：那会向根路径发出一个调用方没打算发的请求
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(null!));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostAsync_BlankUrl_ReportsInvalidUrlBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // POST 落到根路径的后果比 GET 严重（可能创建资源），空白串同样按非法地址拒绝
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("   ", new { a = 1 }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 2) 声明式头绑定的 fail-fast（异常类型沿用 AxiosHeaders.Set 的公开契约）
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeclarativeHeader_InvalidName_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var api = new ApiClient(server.Url);

        // 头名来自 [Header("...")]，非法名在配置构建期就抛（与直接 Set 同一类异常），一个字节都不发
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.RequestAsync(new ProbeBadHeaderNameRequest { Value = "1" }));

        Assert.Empty(server.Received);
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
            client.GetAsync("x", c => c.OnDownloadProgress = _ => throw new InvalidOperationException("boom")));

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
                c => c.OnUploadProgress = _ => throw new InvalidOperationException("boom")));

        // 此前被 HttpClient 包成 HttpRequestException，再翻译成 ERR_NETWORK —— 回调写错被误报成网络故障
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("OnUploadProgress", ex.Message);
    }

    // ------------------------------------------------------------------
    // 4) 回放生效配置：BaseAddress 属于实例来源
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReplayingResponseConfig_UsesTargetInstanceBaseAddress()
    {
        await using var first = TestHttpServer.Start(_ => new TestResponse { Body = Encoding.UTF8.GetBytes("{}") });
        await using var second = TestHttpServer.Start(_ => new TestResponse { Body = Encoding.UTF8.GetBytes("{}") });

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = first.Url });
        var response = await source.GetAsync("x");

        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = second.Url,
            Headers = new AxiosHeaders { ["X-Target"] = "1" }
        });

        // 回放语义：按请求项（Url / Method / 头）保留，实例级项（BaseAddress）改用目标实例的
        await target.RequestAsync(response.Config!);

        var received = Assert.Single(second.Received);
        Assert.Equal("1", received.Headers["X-Target"]);
        Assert.Single(first.Received); // 旧服务端不应再收到第二次请求
    }

    // ------------------------------------------------------------------
    // 5) 自定义 paramsSerializer 的错误模型
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomParamsSerializerThrows_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.ParamsSerializer = _ => throw new InvalidOperationException("bad params")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 6) 附加回归：COW 头存储的模型对比 / Bearer 的 scheme 处理
    // ------------------------------------------------------------------

    /// <summary>
    /// 让 <see cref="AxiosHeaders"/> 的实例树与「朴素字典树」并行执行同一串随机操作，每一步校验全部实例。
    /// COW 的缺陷（副本读到别人的写入、引用计数错导致漏复制）会立刻暴露。
    /// </summary>
    [Fact]
    public void CopyOnWrite_RandomOperations_MatchPlainDictionaryModel()
    {
        var random = new Random(20261022);

        var instances = new List<AxiosHeaders> { new() };
        var models = new List<Dictionary<string, string>>
        {
            new(StringComparer.OrdinalIgnoreCase)
        };

        string[] names = ["A", "B", "C", "D", "E"];
        for (var step = 0; step < 3000; step++)
        {
            var index = random.Next(instances.Count);
            switch (random.Next(4))
            {
                case 0:
                {
                    var name = names[random.Next(names.Length)];
                    var value = "v" + random.Next(100);
                    instances[index].Set(name, value);
                    models[index][name] = value;
                    break;
                }

                case 1:
                {
                    var name = names[random.Next(names.Length)];
                    Assert.Equal(models[index].Remove(name), instances[index].Delete(name));
                    break;
                }

                case 2:
                    instances[index].Clear();
                    models[index].Clear();
                    break;

                default:
                    if (instances.Count < 24)
                    {
                        instances.Add(instances[index].Clone());
                        models.Add(new Dictionary<string, string>(models[index], StringComparer.OrdinalIgnoreCase));
                    }

                    break;
            }

            for (var i = 0; i < instances.Count; i++)
            {
                Assert.Equal(models[i].Count, instances[i].Count);

                foreach (var name in names)
                {
                    models[i].TryGetValue(name, out var expected);
                    Assert.Equal(expected, instances[i].Get(name));
                    Assert.Equal(expected is not null, instances[i].Has(name));
                }
            }
        }
    }

    [Fact]
    public async Task BearerToken_WithAndWithoutScheme_BothSendAsExpected()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());

        using (var api = new ApiClient(server.Url) { Authorization = new BearerAuthorizationProvider("abc") })
        {
            await api.RequestAsync(new ProbeHeaderRequest());
        }

        using (var api = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("Basic dXNlcg==")
        })
        {
            await api.RequestAsync(new ProbeHeaderRequest());
        }

        Assert.Equal("Bearer abc", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Basic dXNlcg==", server.Received[1].Headers["Authorization"]);
    }

    // ------------------------------------------------------------------
    // 7) 标量请求体的头必须跟着生效序列化器（此前恒为 application/json）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScalarBody_TakesMediaTypeFromInjectedSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "x",
            42,
            c => c.ContentSerializer = AxiosNet.Serialization.XmlContentSerializer.Instance);

        var received = Assert.Single(server.Received);
        Assert.StartsWith(AxiosContentType.Xml, received.Headers["Content-Type"]);
        Assert.Equal("42", Encoding.UTF8.GetString(received.Body));
    }

    [Get("probe")]
    public sealed class ProbeHeaderRequest : IApiRequest<string>
    {
        [Header("X-Trace")]
        public string? TraceId { get; set; }
    }

    [Get("probe")]
    public sealed class ProbeBadHeaderNameRequest : IApiRequest<string>
    {
        [Header("X Bad")]
        public string? Value { get; set; }
    }
}
