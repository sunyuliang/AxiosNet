using System.Diagnostics;
using System.Text;
using AxiosNet.Circuit;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 短路响应（<see cref="AxiosShortCircuit"/>）：不发请求、直接交付调用方给的响应。
/// <para>
/// 断言分两侧：<b>跳过</b>的是「一次 HTTP 操作」本身（网络、连接、闸门、重试、熔断），
/// <b>保留</b>的是「响应方向」的语义（按本次类型反序列化、transformResponse、状态码校验、响应拦截器）。
/// 两侧都测，才能保证它不是「另开一条简化通道」。
/// </para>
/// </summary>
public sealed class ShortCircuitTests
{
    /// <summary>等待某个条件成立（最坏 2 秒）；用于「请求已经打到服务器」这类没有回调可挂的时点。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "等待条件成立超时");
    }

    // ------------------------------------------------------------------
    // 不发送
    // ------------------------------------------------------------------

    [Fact]
    public async Task Body_IsReDeserializedPerCall_AndNoRequestIsSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));

        using var client = new AxiosClient();

        // 同一份字节（缓存条目的形态）喂给两个不同的泛型入口：形状必须跟着本次调用变，
        // 这正是「缓存字节而不是对象图」的理由。
        var reply = new AxiosShortCircuit
        {
            Body = TestJson.Bytes(new Person("cached", 7)),
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["X-Cache"] = "HIT" }
        };

        var typed = await client.GetAsync<Person>(server.Url + "users/1", c => c.ShortCircuit = reply);
        var text = await client.GetAsync<string>(server.Url + "users/1", c => c.ShortCircuit = reply);

        Assert.Empty(server.Received);
        Assert.Equal("cached", typed.Data!.Name);
        Assert.Equal(7, typed.Data.Age);
        Assert.Equal("""{"name":"cached","age":7}""", text.Data);
        Assert.Equal(200, typed.Status);
        Assert.Equal("OK", typed.StatusText);
        Assert.Equal("HIT", typed.GetHeader("X-Cache"));

        // 没有发出过请求：不伪造一个 HttpRequestMessage 让调用方以为发过
        Assert.Null(typed.Request);
        Assert.Same(reply, typed.Config!.ShortCircuit);
    }

    [Fact]
    public async Task RequestInterceptor_CanShortCircuit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));
        using var client = new AxiosClient();

        // 这正是引入短路的动机：拦截器只能改配置，没有「直接给响应」的出口。
        client.Interceptors.Request.Use(config =>
        {
            config.ShortCircuit = AxiosShortCircuit.Json("""{"name":"from-interceptor","age":1}""");
            return config;
        });

        var response = await client.GetAsync<Person>(server.Url + "users/1");

        Assert.Empty(server.Received);
        Assert.Equal("from-interceptor", response.Data!.Name);
    }

    [Fact]
    public async Task ShortCircuit_IsNotInheritedNorReplayedFromResponseConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));

        // 实例默认值不生效：短路描述的是「某一次请求」，做成常开的 mock 只会在某天忘了关
        using var mocked = new AxiosClient(new AxiosConfig
        {
            ShortCircuit = AxiosShortCircuit.From(new Person("stale", 0))
        });

        var fromServer = await mocked.GetAsync<Person>(server.Url + "users/1");
        Assert.Equal("server", fromServer.Data!.Name);

        // 回放 response.Config 也不重放那次短路：否则这条请求会永远不发出去
        using var client = new AxiosClient();
        var shortCircuited = await client.GetAsync<Person>(server.Url + "users/1", c =>
            c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 7)));

        var replayed = await client.RequestAsync<Person>(shortCircuited.Config!);

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("server", replayed.Data!.Name);
    }

    // ------------------------------------------------------------------
    // 跳过的是「HTTP 操作」：闸门 / 重试 / 熔断
    // ------------------------------------------------------------------

    [Fact]
    public async Task ShortCircuit_DoesNotQueueForConcurrencySlot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            // 挂住唯一的并发名额，直到短路请求已经返回
            await release.Task;
            return TestJson.Response(new Person("slow", 1));
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            // QueueTimeout = 0：名额被占时排队请求立刻失败 —— 短路要是也走闸门，这个用例就会抛错
            Limits = { MaxConcurrentRequests = 1, QueueTimeout = TimeSpan.Zero }
        });

        var held = client.GetAsync(server.Url + "slow");
        await WaitForAsync(() => server.Received.Count == 1);

        var cached = await client.GetAsync<Person>(server.Url + "cached", c =>
            c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1)));

        release.SetResult();
        await held;

        Assert.Equal("cached", cached.Data!.Name);
    }

    [Fact]
    public async Task RetryPolicy_IsNotApplied()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));

        using var client = new AxiosClient(new AxiosConfig
        {
            // 每次重试等待 200ms：真按策略走的话，这段耗时一眼就能看出来
            Retry = { Policy = new RetryPolicy { MaxRetries = 3, Delay = TimeSpan.FromMilliseconds(200) } }
        });

        var stopwatch = Stopwatch.StartNew();
        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "users/1",
            c => c.ShortCircuit = new AxiosShortCircuit { Status = 500 }));
        stopwatch.Stop();

        Assert.Empty(server.Received);
        Assert.Equal(AxiosException.ErrBadResponse, failed.Code);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 300,
            $"500 的短路响应不该进重试循环（实际耗时 {stopwatch.ElapsedMilliseconds}ms）");
    }

    [Fact]
    public async Task OpenCircuit_DoesNotBlockShortCircuit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("boom", 0), statusCode: 500));

        var breaker = new CircuitBreaker { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(30) };
        using var client = new AxiosClient(new AxiosConfig { Circuit = { Policy = breaker } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "users/1"));
        Assert.Equal(AxiosCircuitState.Open, breaker.GetState($"127.0.0.1:{server.Port}"));

        // 熔断打开后短路仍然成立：它不经过发送阶段，也就没有「要不要放行」这一步
        var cached = await client.GetAsync<Person>(server.Url + "users/1", c =>
            c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1)));

        Assert.Equal("cached", cached.Data!.Name);
        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // 保留的是「响应方向」：transformResponse / 拦截器 / 错误模型
    // ------------------------------------------------------------------

    [Fact]
    public async Task TransformResponse_RunsForBody_ButNotForMaterializedData()
    {
        using var client = new AxiosClient();
        const string url = "http://127.0.0.1:9/never";

        static object? Bump(object? data, AxiosConfig _)
            => data is Person person ? person with { Age = 99 } : data;

        // Body 是「正文」：与网络路径一样过 transformResponse
        var fromBody = await client.GetAsync<Person>(url, c =>
        {
            c.ShortCircuit = AxiosShortCircuit.Json("""{"name":"a","age":1}""");
            c.TransformResponse.Add(Bump);
        });

        Assert.Equal(99, fromBody.Data!.Age);

        // Data 是「已成型的结果」：再加工一次等于让 transform 执行次数取决于缓存状态
        var stub = new Person("b", 1);
        var fromData = await client.GetAsync<Person>(url, c =>
        {
            c.ShortCircuit = AxiosShortCircuit.From(stub);
            c.TransformResponse.Add(Bump);
        });

        Assert.Same(stub, fromData.Data);
    }

    [Fact]
    public async Task ResponseInterceptors_RunOnShortCircuit()
    {
        using var client = new AxiosClient();

        var seen = 0;
        client.Interceptors.Response.Use(response =>
        {
            seen++;
            response.Status = 207;
            return response;
        });

        var response = await client.GetAsync<Person>("http://127.0.0.1:9/never", c =>
            c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1)));

        Assert.Equal(1, seen);
        Assert.Equal(207, response.Status);
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsLikeANetworkResponse_AndCanBeRecoveredByInterceptor()
    {
        using var client = new AxiosClient();
        const string url = "http://127.0.0.1:9/never";

        var notFound = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            url,
            c => c.ShortCircuit = new AxiosShortCircuit
            {
                Status = 404,
                StatusText = "Not Found",
                ContentType = "text/html; charset=utf-8",
                Body = Encoding.UTF8.GetBytes("<html>nope</html>")
            }));

        Assert.Equal(AxiosException.ErrBadRequest, notFound.Code);
        Assert.Equal(404, notFound.Status);
        Assert.Equal("Not Found", notFound.Response!.StatusText);

        // 状态码不合法走宽松解析：正文原样交回，不会被「按 JSON 解析失败」吞掉
        Assert.Equal("<html>nope</html>", notFound.Response.Data);

        // 4xx 与 5xx 的错误码分档与网络路径一致
        var serverError = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            url,
            c => c.ShortCircuit = new AxiosShortCircuit { Status = 503 }));
        Assert.Equal(AxiosException.ErrBadResponse, serverError.Code);

        // 拦截器的 OnRejected 照常兜住：与真实响应同一条责任链
        client.Interceptors.Response.Use(
            response => response,
            _ => new AxiosResponse { Status = 200, Data = new Person("fallback", 1) });

        var recovered = await client.GetAsync<Person>(url, c =>
            c.ShortCircuit = new AxiosShortCircuit { Status = 500 });

        Assert.Equal("fallback", recovered.Data!.Name);
    }

    [Fact]
    public async Task ValidateStatus_CanAcceptNonSuccessStatus()
    {
        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>("http://127.0.0.1:9/never", c =>
        {
            c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1), status: 304);
            c.Callbacks.ValidateStatus = status => status is >= 200 and < 400;
        });

        Assert.Equal(304, response.Status);
        Assert.Equal("cached", response.Data!.Name);
    }

    // ------------------------------------------------------------------
    // 边界
    // ------------------------------------------------------------------

    [Fact]
    public async Task ByteArrayBody_IsCopiedPerCall()
    {
        using var client = new AxiosClient();
        const string url = "http://127.0.0.1:9/never";

        var body = new byte[] { 1, 2, 3 };
        var reply = new AxiosShortCircuit { Body = body, ContentType = "application/octet-stream" };

        var first = await client.GetAsync<byte[]>(url, c => c.ShortCircuit = reply);
        var second = await client.GetAsync<byte[]>(url, c => c.ShortCircuit = reply);

        // 每份响应都是独立对象：缓存条目不该被某一次调用的写入污染
        Assert.NotSame(first.Data, second.Data);
        Assert.NotSame(body, first.Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Data!);
    }

    [Fact]
    public async Task StreamResponseType_GetsItsOwnMemoryStream()
    {
        using var client = new AxiosClient();
        const string url = "http://127.0.0.1:9/never";

        var reply = AxiosShortCircuit.Json("""{"name":"cached","age":1}""");

        var first = await client.GetAsync<Stream>(url, c => c.ShortCircuit = reply);
        var second = await client.GetAsync<Stream>(url, c => c.ShortCircuit = reply);

        // 每次新建：缓存里的字节数组不随某一条流的所有权漂移
        Assert.NotSame(first.Data, second.Data);

        // 独立的不只流对象本身：底层字节数组也必须复制 —— 改写缓存条目后，已交付的流不应跟着变
        var expected = """{"name":"cached","age":1}""";
        var cached = reply.Body!;
        cached[0] = (byte)'X';

        using var reader = new StreamReader(first.Data!);
        Assert.Equal(expected, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task BodyAndData_AreMutuallyExclusive()
    {
        using var client = new AxiosClient();

        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "http://127.0.0.1:9/never",
            c => c.ShortCircuit = new AxiosShortCircuit
            {
                Body = Array.Empty<byte>(),
                Data = new Person("x", 1)
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, failed.Code);
        Assert.Contains("Body", failed.Message);
    }

    [Fact]
    public async Task InvalidStatus_IsRejectedAsOptionError()
    {
        using var client = new AxiosClient();

        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "http://127.0.0.1:9/never",
            c => c.ShortCircuit = new AxiosShortCircuit { Status = 42 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, failed.Code);
    }

    [Fact]
    public async Task CanceledToken_WinsOverCacheHit()
    {
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 同一个调用点，短路与否不该改变「已取消」的结论
        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(
            "http://127.0.0.1:9/never",
            c => c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1)),
            cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, failed.Code);
    }

    [Fact]
    public async Task MaterializedData_MustMatchTargetType()
    {
        using var client = new AxiosClient();

        // 与「responseType 与泛型参数不匹配」同一个错误码：延迟到 Data getter 抛会炸得很远
        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(
            "http://127.0.0.1:9/never",
            c => c.ShortCircuit = AxiosShortCircuit.From("not a person")));

        Assert.Equal(AxiosException.ErrBadResponse, failed.Code);
    }

    [Fact]
    public async Task ContentType_OverridesHeaders_AndDrivesDecoding()
    {
        using var client = new AxiosClient();

        // 非 UTF-8 正文：字符集必须一路带到解码层，丢了它命中路径就会乱码
        // （用 Latin1 而不是 GBK：后者在 .NET Core 上要额外注册 CodePages 提供程序，测试基建不该依赖它）
        var reply = new AxiosShortCircuit
        {
            Body = Encoding.Latin1.GetBytes("café"),
            ContentType = "text/plain; charset=iso-8859-1",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain; charset=utf-8" }
        };

        var response = await client.GetAsync<string>("http://127.0.0.1:9/never", c => c.ShortCircuit = reply);

        Assert.Equal("café", response.Data);
        Assert.Equal("text/plain; charset=iso-8859-1", response.GetHeader("Content-Type"));
        Assert.Equal("text/plain", response.Config!.Response!.MediaType);
        Assert.Equal("iso-8859-1", response.Config.Response.CharSet);
    }

    [Fact]
    public async Task InvalidRangeOption_IsStillRejected()
    {
        using var client = new AxiosClient();

        // 范围校验判的是「这份配置是否可执行」，与有没有发出请求无关：命中不该让 Timeout = -1 静默通过，
        // 否则同一个 bug 在网络路径报错、在命中路径不报错 —— 报不报取决于缓存状态。
        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "http://127.0.0.1:9/never",
            c =>
            {
                c.Timeout = TimeSpan.FromSeconds(-1);
                c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 1));
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, failed.Code);
    }

    [Fact]
    public async Task InvalidHeaderName_IsRejectedAsOptionError()
    {
        using var client = new AxiosClient();

        // 头名与头值各过一道闸：值挡 C0 注入，名挡非 token。短路响应的头会被写进日志、
        // 喂给 transformResponse，与请求头同一把尺子 —— 否则这里能塞进一条「长得像两行」的假头。
        var failed = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            "http://127.0.0.1:9/never",
            c => c.ShortCircuit = new AxiosShortCircuit
            {
                Body = Encoding.UTF8.GetBytes("{}"),
                ContentType = "application/json",
                Headers = new Dictionary<string, string> { ["X-Bad\r\nX-Inject"] = "1" }
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, failed.Code);
    }

    [Fact]
    public async Task ShortCircuit_EmitsNoTelemetrySpan()
    {
        // 服务器只在「万一真发了」时才会被写：既给探针一个端口，也顺带锁住「没发出去」
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));
        var path = "short-circuit/" + Guid.NewGuid().ToString("N");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient();
        var response = await client.GetAsync<Person>(
            server.Url + path,
            c => c.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 7)));

        probe.CollectObservable();

        Assert.Equal("cached", response.Data!.Name);

        // 一次没发生的 HTTP 操作不该在 trace / 事件流里留下一条没有对端、没有耗时的幽灵请求；
        // 命中率由调用方的缓存自己统计（README 的「跳过」表里写明这一条）。
        Assert.Empty(probe.Spans);
        Assert.Empty(probe.Events);

        // 顺带：一次都没发出去（探针的过滤依赖路径，真发了就会被上面两条抓到）
        Assert.Empty(server.Received);
    }
}
