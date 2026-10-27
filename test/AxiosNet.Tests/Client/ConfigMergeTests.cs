
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 配置合并、克隆与集合契约：`Defaults` -> 本次请求的覆盖、`*Set` 门禁、集合隔离。
/// </summary>
public sealed class ConfigMergeTests
{
    [Fact]
    public async Task ReplayResponseConfig_AppliesTargetInstanceDefaultHeaders()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var first = await source.GetAsync("x");

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        target.Defaults.Headers.Set("X-Instance", "target");

        await target.RequestAsync(first.Config!);

        var replayed = server.Received[1];
        Assert.Equal("target", replayed.Headers["X-Instance"]);
    }

    [Fact]
    public async Task ReplayResponseConfig_DoesNotFalselyRejectInstanceLevelOptions()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        // 源实例把 maxRedirects 定为 3，进的是连接池 Handler。
        using var source = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                MaxRedirects = 3,
            },
        });
        var first = await source.GetAsync("x");

        // 回放不是「按请求写 MaxRedirects」，实例级选项应归目标实例。
        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var replayed = await target.RequestAsync(first.Config!);

        Assert.Equal(200, replayed.Status);
        Assert.Equal(5, replayed.Config!.Connection.MaxRedirects);
    }

    [Fact]
    public async Task ExplicitResponseType_SurvivesReplay()
    {
        // 调用方显式设置的 responseType 是真实意图，回放保留。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var r1 = await client.GetAsync("x", c => c.ResponseType = ResponseType.Text);
        Assert.Equal(""""{"name":"n","age":1}"""", r1.Data);

        var r2 = await client.RequestAsync(r1.Config!);
        Assert.Equal(ResponseType.Text, r2.Config!.ResponseType);
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

    [Fact]
    public void ConfigClone_ListIsolation_HoldsInBothDirections()
    {
        var source = new AxiosConfig();
        source.TransformRequest.Add((data, _) => data);
        source.RemoveHeaders.Add("X-A");

        // 副本写：写时复制分离，源不受影响
        var clone = source.Clone();
        clone.TransformRequest.Add((data, _) => data);
        clone.RemoveHeaders.Add("X-B");

        Assert.Single(source.TransformRequest);
        Assert.Single(source.RemoveHeaders);
        Assert.Equal(2, clone.TransformRequest.Count);
        Assert.Equal(2, clone.RemoveHeaders.Count);

        // 源写：同样分离 —— 已克隆出去的副本看不到后续写入
        // （与旧「每请求整份拷贝」语义一致：隔离不靠副本，靠写入前分离）
        var snapshot = source.Clone();
        source.TransformRequest.Add((data, _) => data);
        source.RemoveHeaders.Add("X-C");

        Assert.Single(snapshot.TransformRequest);
        Assert.Single(snapshot.RemoveHeaders);
        Assert.Equal(2, source.TransformRequest.Count);
        Assert.Equal(2, source.RemoveHeaders.Count);
    }

    // ---------------------------------------------------------------
    // 9：回放不带上一次的按请求 Auth
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReplayedEffectiveConfig_DoesNotCarryPerRequestAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var response = await source.GetAsync("x", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        Assert.Equal(TestJson.BasicAuth("user", "pass"), server.Received[0].Headers["Authorization"]);

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // Auth 是那一次请求的意图，不是「这份配置的内容」：回放不能把它带上
        await target.RequestAsync(response.Config!);

        Assert.False(server.Received[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ReplayedEffectiveConfig_UsesTargetInstanceDefaultAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var response = await source.GetAsync("x", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new() { Auth = new AxiosAuth("target", "tpw") },
        });

        await target.RequestAsync(response.Config!);

        // 目标实例的默认凭据生效（既不是源实例的按请求凭据，也不是空）
        Assert.Equal(TestJson.BasicAuth("target", "tpw"), server.Received[1].Headers["Authorization"]);
    }

    // ------------------------------------------------------------------
    // 回放语义：CancellationToken 与 Auth
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_ResponseConfig_DoesNotCarrySourceCancellationToken()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var clientA = new AxiosClient(handler: handler);
        using var cts = new CancellationTokenSource();

        var response = await clientA.GetAsync("http://localhost/a", c => c.CancellationToken = cts.Token);
        var config = response.Config!;
        Assert.Equal(cts.Token, config.CancellationToken);

        // 回放到同一实例与另一个实例：令牌描述的是「来源实例那一次调用」，不搬过去
        // （观测点是回放请求自己的生效配置，不是 handler 收到的内部 CTS 令牌）。
        var replaySame = await clientA.GetAsync("http://localhost/a", config);
        Assert.False(replaySame.Config!.CancellationToken.CanBeCanceled, "回放到同实例后仍带着来源令牌。");

        var handlerB = new RecordingHandler(_ => JsonResponse());
        using var clientB = new AxiosClient(handler: handlerB);
        var replayOther = await clientB.GetAsync("http://localhost/b", config);
        Assert.False(replayOther.Config!.CancellationToken.CanBeCanceled, "回放到另一实例时把来源令牌搬了过去。");
    }

    // 与 List<T> 逐条对齐：同一操作抛同一异常类型。

    [Fact]
    public void OutOfRangeOperations_ThrowSameTypeAsList()
    {
        var cases = new (string Name, Action Ours, Action Baseline)[]
        {
            ("空表 RemoveAt(0)", Chain(l => l.RemoveAt(0)), () => new List<int>().RemoveAt(0)),
            ("空表 RemoveAt(-1)", Chain(l => l.RemoveAt(-1)), () => new List<int>().RemoveAt(-1)),
            ("1 项 RemoveAt(1)", Chain(l => l.RemoveAt(1), seed: true), () => new List<int> { 1 }.RemoveAt(1)),
            ("空表 this[0] 读", Chain(l => _ = l[0]), () => _ = new List<int>()[0]),
            ("空表 this[5] 读", Chain(l => _ = l[5]), () => _ = new List<int>()[5]),
            ("1 项 this[-1] 读", Chain(l => _ = l[-1], seed: true), () => _ = new List<int> { 1 }[-1]),
            ("1 项 this[9] 读", Chain(l => _ = l[9], seed: true), () => _ = new List<int> { 1 }[9]),
            ("1 项 this[9] 写", Chain(l => l[9] = One, seed: true), () =>
            {
                var baseline = new List<int> { 1 };
                baseline[9] = 0;
            }),
            ("空表 Insert(1, x)", Chain(l => l.Insert(1, One)), () => new List<int>().Insert(1, 0)),
            ("空表 Insert(-1, x)", Chain(l => l.Insert(-1, One)), () => new List<int>().Insert(-1, 0)),
            ("1 项 Insert(9, x)", Chain(l => l.Insert(9, One), seed: true), () => new List<int> { 1 }.Insert(9, 0)),
        };

        foreach (var (name, ours, baseline) in cases)
        {
            var expected = Record(baseline);
            var actual = Record(ours);

            Assert.True(expected == actual, $"{name}：本类型抛 {actual}，List<T> 抛 {expected} —— 契约不一致。");
            Assert.Equal(nameof(ArgumentOutOfRangeException), actual);
        }
    }

    // 参数名与取值。

    [Fact]
    public void RemoveAtOnEmpty_ReportsIndexParameterAndValue()
    {
        var list = new AxiosConfig().TransformRequest;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(0));

        Assert.Equal("index", ex.ParamName);
        Assert.Equal(0, ex.ActualValue);
    }

    [Fact]
    public void IndexerOutOfRange_ReportsIndexParameter()
    {
        var list = new AxiosConfig().TransformRequest;

        var read = Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[3]);
        var write = Assert.Throws<ArgumentOutOfRangeException>(() => list[3] = One);

        Assert.Equal("index", read.ParamName);
        Assert.Equal("index", write.ParamName);
    }

    [Fact]
    public void RemoveHeaders_RemoveAtOutOfRange_ThrowsArgumentOutOfRange()
    {
        // 删头名单是另一个公开集合（ICollection<string>），走的是同一个 SynchronizedList。
        var headers = new AxiosConfig().RemoveHeaders;
        headers.Add("X-Trace");

        var list = (IList<string>)headers;

        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(-1));
        Assert.Single(headers);
    }

    // 合法边界不能被误伤。

    [Fact]
    public void InsertAtCount_Appends()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);

        list.Insert(list.Count, One); // index == Count 合法（追加）

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void RemoveAtLastIndex_Succeeds()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);
        list.Add(One);

        list.RemoveAt(1);

        Assert.Single(list);
    }

    [Fact]
    public void IndexerWithinRange_StillReadsAndWrites()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);

        list[0] = One;

        Assert.Same(One, list[0]);
    }

    // ---------------------------------------------------------------
    // *Set 语义：显式 null 覆盖实例默认上限
    // ---------------------------------------------------------------

    [Fact]
    public async Task PerRequestMaxContentLengthNull_ClearsInstanceDefault()
    {
        var payload = new byte[4096];
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxContentLength = 16,
            },
        });

        // 实例默认上限对 4KB 响应生效（声明了长度 → 读正文前就拒绝）
        var blocked = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(server.Url + "file"));
        Assert.Equal(AxiosException.ErrBadResponse, blocked.Code);

        // 按请求显式设 null 必须清掉它：这条路径完全依赖 MaxContentLengthSet，
        // 一旦合并退化成「null 即未设置」，这里会继续被实例默认拦住。
        var response = await client.GetAsync<byte[]>(server.Url + "file", c => c.Limits.MaxContentLength = null);

        Assert.Equal(payload.Length, response.Data!.Length);
    }

    [Fact]
    public async Task PerRequestMaxBodyLengthNull_ClearsInstanceDefault()
    {
        var payload = new byte[1024];
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxBodyLength = 16,
            },
        });

        var blocked = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", payload));
        Assert.Equal(AxiosException.ErrBadRequest, blocked.Code);
        Assert.Empty(server.Received);

        var response = await client.PostAsync(server.Url + "up", payload, c => c.Limits.MaxBodyLength = null);

        Assert.Equal(200, response.Status);
        Assert.Equal(payload.Length, server.Received[^1].Body.Length);
    }

    [Fact]
    public async Task PerRequestMaxRateNull_ClearsInstanceDefault()
    {
        const int size = 4096;
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[size], ContentType: "application/octet-stream"));

        // 实例默认 32 B/s：4KB 需要 128 秒，3 秒预算下必然超时。
        // 清不掉实例限速时本用例会以 ECONNABORTED 失败，而不是变慢。
        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxRate = 32,
            },
            Timeout = TimeSpan.FromSeconds(3),
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync<byte[]>(server.Url + "file", c => c.Limits.MaxRate = null);
        stopwatch.Stop();

        Assert.Equal(size, response.Data!.Length);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"限速未被清除：{stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 回放 <see cref="AxiosResponse.Config"/>：它本身是上一次的生效配置，但交回来时必须被当成
    /// 按请求项重新合并到本实例默认值上（合并标记只在管线内部有效，不随 <c>Clone</c> 继承）。
    /// </summary>
    [Fact]
    public async Task ReplayedEffectiveConfig_IsMergedIntoTheNewClientDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var first = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                PoolOptions = new ConnectionPoolOptions(),
            },
        });
        first.Defaults.Headers.Set("X-First", "1");
        var response = await first.GetAsync("one");

        using var second = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        second.Defaults.Headers.Set("X-Second", "2");

        // 不抛 ERR_BAD_OPTION（PoolOptions 是第一个实例的默认值，不是本次按请求写的）
        await second.PostAsync("two", response.Config!);

        var received = server.Received[1];

        // 新实例的默认头必须生效
        Assert.Equal("2", received.Headers["X-Second"]);

        // 旧实例的生效头仍在：合并后的配置里「实例默认」与「按请求项」已经不可区分，
        // 回放只能做到「带着旧值再合并一次」，这里把这条语义固定下来。
        Assert.Equal("1", received.Headers["X-First"]);
    }

    [Fact]
    public void Clone_CopiesMutableDictionaryContainers()
    {
        var sentinel = new object();
        var defaults = new AxiosConfig
        {
            Params = new Dictionary<string, object?> { ["p"] = sentinel },
            Data = new Dictionary<string, object?> { ["d"] = 1 }
        };

        var clone = defaults.Clone();

        ((Dictionary<string, object?>)clone.Params!)["added"] = 2;
        ((Dictionary<string, object?>)clone.Data!)["added"] = 2;

        Assert.Single((Dictionary<string, object?>)defaults.Params!);
        Assert.Single((Dictionary<string, object?>)defaults.Data!);

        // 元素仍按引用共享。
        Assert.Same(sentinel, ((Dictionary<string, object?>)clone.Params!)["p"]);
    }

    [Fact]
    public void Clone_PreservesDictionaryFamilyAndComparer()
    {
        var defaults = new AxiosConfig
        {
            Params = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Api_Key"] = 1 },
            Data = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 }
        };

        var clone = defaults.Clone();

        var parameters = Assert.IsType<Dictionary<string, object?>>(clone.Params);
        Assert.True(parameters.ContainsKey("api_key"));

        var body = Assert.IsType<SortedDictionary<string, object?>>(clone.Data);
        Assert.True(body.ContainsKey("a"));

        parameters["added"] = 2;
        Assert.Single((Dictionary<string, object?>)defaults.Params!);
    }

    [Fact]
    public void Clone_PreservesConcurrentDictionary()
    {
        var source = new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        source["Key"] = 1;

        var clone = new AxiosConfig { Params = source }.Clone();

        var parameters = Assert.IsType<ConcurrentDictionary<string, object?>>(clone.Params);
        Assert.NotSame(source, parameters);
        Assert.True(parameters.ContainsKey("key"));
    }

    [Fact]
    public async Task NullParamsAndData_ClearInheritedDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Data = new { inherited = true },
            Params = new { inherited = 1 }
        });

        // 按请求显式写 null 才会摘掉实例默认值。
        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "x",
            Method = HttpMethod.Post,
            Data = null,
            Params = null
        });

        Assert.Equal(200, response.Status);
        var received = Assert.Single(server.Received);
        Assert.Equal("/x", received.Path);
        Assert.Empty(received.Body);
    }

    [Fact]
    public void Clone_AfterFailedNullAssignment_StillWorks()
    {
        var config = new AxiosConfig { BaseAddress = "http://a.com" };
        Assert.Throws<ArgumentNullException>(() => config.Headers = null!);

        var clone = config.Clone();

        Assert.NotNull(clone.Headers);
        Assert.Equal("http://a.com", clone.BaseAddress);
    }

    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // 表单 / multipart / params 展平的环检测。
    private sealed class Node
    {
        public string? Name { get; set; }
        public List<Node> Children { get; } = new();
    }


    private sealed class BlockingDisposeHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }

    // IReadOnlyDictionary 按键值对展平。


    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private static HttpResponseMessage JsonResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestJson.Bytes(new Person("alice", 1)))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
            }
        };


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }


    private static readonly Func<object?, AxiosConfig, object?> One = static (data, _) => data;

    /// <summary>在全新的 transform 链上执行操作（seed 为 true 时先塞一条，用于「有元素但越界」的用例）。</summary>
    private static Action Chain(Action<IList<Func<object?, AxiosConfig, object?>>> action, bool seed = false)
        => () =>
        {
            var list = new AxiosConfig().TransformRequest;
            if (seed)
            {
                list.Add(One);
            }

            action(list);
        };

    private static string Record(Action action)
    {
        try
        {
            action();
            return "<未抛异常>";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }


}