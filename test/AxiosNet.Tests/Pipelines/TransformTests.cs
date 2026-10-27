
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 数据转换：`transformRequest` / `transformResponse` 链的顺序、继承与回放。
/// </summary>
public sealed class TransformTests
{
    [Fact]
    public async Task ReplayResponseConfig_RunsTargetInstanceTransformChain()
    {
        await using var server = TestHttpServer.Start(_ => Json());

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var first = await source.GetAsync("x");

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        target.Defaults.TransformResponse.Add((_, _) => "unwrapped-by-target");

        var replayed = await target.RequestAsync(first.Config!);

        Assert.Equal("unwrapped-by-target", replayed.Data);
    }

    // 回放 response.Config：实例默认 transform 链每请求执行一次。
    [Fact]
    public async Task ReplayResponseConfig_InstanceResponseTransformRunsOncePerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var instanceCalls = 0;
        client.Defaults.TransformResponse.Add((data, _) =>
        {
            instanceCalls++;
            return data;
        });

        var first = await client.GetAsync("x");
        Assert.Equal(1, instanceCalls);

        // 回放一次，Defaults 链只再执行一次。
        await client.RequestAsync(first.Config!);
        Assert.Equal(2, instanceCalls);

        // 连续回放不再累积。
        var second = await client.GetAsync("x");
        await client.RequestAsync(second.Config!);
        Assert.Equal(4, instanceCalls);
    }

    [Fact]
    public async Task ReplayResponseConfig_InstanceRequestTransformRunsOncePerRequest()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var instanceCalls = 0;
        client.Defaults.TransformRequest.Add((data, _) =>
        {
            instanceCalls++;
            return data;
        });

        var first = await client.PostAsync("echo", "x");
        Assert.Equal(1, instanceCalls);

        await client.PostAsync("echo", first.Config!);
        Assert.Equal(2, instanceCalls);
    }

    [Fact]
    public async Task ReplayAcrossClients_TargetAndSourceTransformsEachRunOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var sourceDefaultCalls = 0;
        var perRequestCalls = 0;
        source.Defaults.TransformResponse.Add((data, _) =>
        {
            sourceDefaultCalls++;
            return data;
        });

        var first = await source.GetAsync("x", c => c.TransformResponse.Add((data, _) =>
        {
            perRequestCalls++;
            return data;
        }));
        Assert.Equal(1, sourceDefaultCalls);
        Assert.Equal(1, perRequestCalls);

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var targetDefaultCalls = 0;
        target.Defaults.TransformResponse.Add((data, _) =>
        {
            targetDefaultCalls++;
            return data;
        });

        await target.RequestAsync(first.Config!);

        // 目标实例默认链执行一次；源实例的链作为按请求项各执行一次，均不重复。
        Assert.Equal(1, targetDefaultCalls);
        Assert.Equal(2, perRequestCalls);
        Assert.Equal(2, sourceDefaultCalls);
    }

    [Fact]
    public async Task PerRequestTransform_StillAppendedToInstanceChain()
    {
        // 普通按请求配置的合并语义：实例默认在前、本次追加在后。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformRequest.Add((data, _) => "[" + data + "]");

        await client.PostAsync("echo", "x", c => c.TransformRequest.Add((data, _) => data + "P"));

        Assert.Equal("[x]P", Body(server.Received[0]));
        Assert.Single(client.Defaults.TransformRequest);
    }

    [Fact]
    public async Task ReplayAfterClearingChain_DoesNotDuplicateOrCrash()
    {
        // 回放前整链替换：自己的 transform 只执行一次。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformResponse.Add((data, _) => "instance");
        var first = await client.GetAsync("x");

        var ownCalls = 0;
        var replayConfig = first.Config!.Clone();
        replayConfig.TransformResponse.Clear();
        replayConfig.TransformResponse.Add((data, _) =>
        {
            ownCalls++;
            return data;
        });

        var replayed = await client.RequestAsync(replayConfig);

        Assert.Equal(1, ownCalls);
        Assert.NotNull(replayed.Data);
    }

    // ResponseType 不随回放泄漏。
    [Fact]
    public async Task ReplayObjectTypedConfig_DoesNotLeakDynamicResponseType()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 7)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // <object> 会自动切到 Dynamic。
        var r1 = await client.RequestAsync<object>(new AxiosConfig { Url = "x" });
        Assert.Equal(ResponseType.Dynamic, r1.Config!.ResponseType);

        // 换泛型参数回放：能正常反序列化成 Person。
        var r2 = await client.RequestAsync<Person>(r1.Config!);
        Assert.NotNull(r2.Data);
        Assert.Equal("n", r2.Data!.Name);
        Assert.Equal(7, r2.Data.Age);
    }

    [Fact]
    public async Task ReplayObjectTypedConfig_AsObject_StillGetsDynamic()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 7)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var r1 = await client.RequestAsync<object>(new AxiosConfig { Url = "x" });
        var r2 = await client.RequestAsync<object>(r1.Config!);

        // 同样按 <object> 回放：动态展开语义不变。
        Assert.NotNull(r2.Data);
    }

    [Fact]
    public async Task ReplayStreamTypedConfig_DoesNotLeakStreamResponseType()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 7)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var r1 = await client.GetAsync<Stream>("x");
        Assert.Equal(ResponseType.Stream, r1.Config!.ResponseType);
        r1.Data!.Dispose();

        var r2 = await client.GetAsync<Person>("x", r1.Config!);
        Assert.NotNull(r2.Data);
        Assert.Equal("n", r2.Data!.Name);
    }

    // ------------------------------------------------------------------
    // transform 链：setter 传任何 IList 都要保证线程安全（枚举走快照）
    // ------------------------------------------------------------------

    [Fact]
    public async Task TransformRequest_SetterWrapsPlainList()
    {
        var list = new List<Func<object?, AxiosConfig, object?>> { (data, _) => data };
        var config = new AxiosConfig { TransformRequest = list };

        // 存进去的不是调用方那个 List：否则并发枚举会抛 InvalidOperationException
        Assert.NotSame(list, config.TransformRequest);
        Assert.Single(config.TransformRequest);

        var failures = 0;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    foreach (var _ in config.TransformRequest)
                    {
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        for (var i = 0; i < 5000; i++)
        {
            config.TransformRequest.Add((data, _) => data);
            if (config.TransformRequest.Count > 3)
            {
                config.TransformRequest.RemoveAt(0);
            }
        }

        stop.Cancel();
        await reader;

        Assert.Equal(0, failures);
    }

    [Fact]
    public void TransformResponse_SetterWrapsPlainList()
    {
        var list = new List<Func<object?, AxiosConfig, object?>> { (data, _) => data };
        var config = new AxiosConfig { TransformResponse = list };

        Assert.NotSame(list, config.TransformResponse);
        Assert.Single(config.TransformResponse);

        // 已是包装类型时不重复包装
        var existing = config.TransformResponse;
        config.TransformResponse = existing;
        Assert.Same(existing, config.TransformResponse);
    }

    // ---------------------------------------------------------------
    // 5/6：transform 链写时复制
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConcurrentRequests_PerRequestTransforms_KeepChainsIsolated()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 实例默认链 + 每请求追加：写时复制下并发请求各自分离，互不串味也不污染 Defaults
        client.Defaults.TransformRequest.Add((data, _) => "[" + data + "]");

        var requests = Enumerable.Range(0, 32).Select(i => client.PostAsync(
            "echo",
            "x",
            c => c.TransformRequest.Add((data, _) => data + i.ToString(CultureInfo.InvariantCulture))));

        await Task.WhenAll(requests);

        Assert.All(server.Received, r => Assert.Matches(@"^\[x\]\d+$", BodyOf(r)));
        Assert.Single(client.Defaults.TransformRequest);   // 实例链不被按请求追加污染
    }

    [Fact]
    public async Task Transform_DuplicateRegistrationInSameConfig_IsNotSwallowed()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        // 调用方 new 的 config（非库产出）：两次追加与默认链同引用的委托是「有意重复」，
        // 合并后应执行 3 次（默认 1 + 本次 2）—— 去重只覆盖 perCall 登记的继承段。
        var config = new AxiosConfig { Data = new { x = 1 } };
        config.TransformRequest.Add(log);
        config.TransformRequest.Add(log);
        await client.PostAsync("http://localhost/a", config);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Transform_Replay_DoesNotDuplicateDefaultChain()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        var response = await client.GetAsync("http://localhost/a");
        Assert.Equal(1, calls);

        // 回放生效配置：默认链不重复执行（继承段去重）
        await client.GetAsync("http://localhost/a", response.Config!);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Transform_ReplayOfObjectEntryResponse_DoesNotDuplicateDefaultChain()
    {
        // 对象入口（调用方自建配置、自身不带任何 transform）走 MergeConfig：
        // 产物的继承段必须仍记为「实例默认段」，否则回放时默认 transform 会再执行一遍。
        // 只在 perCall.HasTransform* 分支里写产物标记时，这条路径的标记恒为 0（回归点）。
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = "http://localhost/a",
            Data = new { x = 1 }
        });
        Assert.Equal(1, calls);

        await client.RequestAsync(response.Config!);
        Assert.Equal(2, calls);
    }

    // ---------------------------------------------------------------
    // transform 链：顺序固定为「实例默认 → 本次追加」，且不写回实例
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestTransformChain_RunsInstanceThenPerRequest_AndDoesNotAccumulate()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformRequest.Add((data, _) => "[" + data + "]");

        var perRequestCalls = 0;
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync("echo", "x", c => c.TransformRequest.Add((data, _) =>
            {
                perRequestCalls++;
                return data + "P";
            }));
        }

        // 顺序：实例默认的包裹在前，本次追加的拼接在后
        Assert.Equal("[x]P", BodyOf_ConfigMergeContract(server.Received[0]));
        Assert.Equal("[x]P", BodyOf_ConfigMergeContract(server.Received[2]));

        // 每次请求只跑一次本次追加的 transform（若合并写回了 Defaults，第 2、3 次会重复叠加）
        Assert.Equal(3, perRequestCalls);
        Assert.Single(client.Defaults.TransformRequest);
    }

    [Fact]
    public async Task ResponseTransformChain_RunsInstanceThenPerRequest_InOrder()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { v = 1 }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformResponse.Add((data, _) => "I:" + data);

        var response = await client.GetAsync<string>("data", c =>
            c.TransformResponse.Add((data, _) => data + ":P"));

        // 非泛型链上返回的新实例也会带上处理过的 Data
        Assert.Equal("I:{\"v\":1}:P", response.Data);

        // 实例默认链没有被本次追加污染
        Assert.Single(client.Defaults.TransformResponse);
    }

    [Fact]
    public async Task TransformLists_AreSafeUnderConcurrentMutation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var stop = new CancellationTokenSource();

        Func<object?, AxiosConfig, object?> identity = static (data, _) => data;

        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                client.Defaults.TransformRequest.Add(identity);
                client.Defaults.TransformResponse.Add(static (data, _) => data);
                client.Defaults.TransformRequest.Remove(identity);
                client.Defaults.TransformResponse.RemoveAt(0);
            }
        });

        var failures = new List<Exception>();
        for (var i = 0; i < 300; i++)
        {
            try
            {
                await client.GetAsync(server.Url + "ping");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        stop.Cancel();
        await writer;

        Assert.Empty(failures);
    }

    [Fact]
    public async Task TransformRequest_RunsBeforeEncoding()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "echo", new { secret = "raw" }, c =>
        {
            c.TransformRequest.Add((data, _) => new { masked = "***" });
            c.TransformRequest.Add((data, _) =>
            {
                // 第二个 transform 拿到的是上一个 transform 的返回值
                Assert.NotNull(data);
                return data;
            });
        });

        Assert.Equal("""{"masked":"***"}""", Body_SerializationAndTransform(server.Received[0]));
    }

    [Fact]
    public async Task TransformResponse_ProcessesParsedData()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { code = 0, data = new { name = "alice" } }));
        using var client = new AxiosClient();

        var response = await client.GetAsync<System.Text.Json.JsonElement>(server.Url + "wrap", c =>
            c.TransformResponse.Add((data, _) =>
                ((System.Text.Json.JsonElement)data!).GetProperty("data")));

        Assert.Equal("alice", response.Data.GetProperty("name").GetString());
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

    private static string BodyOf(TestRequest request) => Encoding.UTF8.GetString(request.Body);

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


    private static string BodyOf_ConfigMergeContract(TestRequest request) => Encoding.UTF8.GetString(request.Body);


    /// <summary>
    /// net8.0 内置 <c>JsonNamingPolicy.SnakeCaseLower</c>，net6.0 的 System.Text.Json 6 没有，
    /// 这里给出等价实现，保证两个 TFM 的用例语义一致。
    /// </summary>
    private static JsonNamingPolicy SnakeCase =>
#if NET8_0_OR_GREATER
        JsonNamingPolicy.SnakeCaseLower;

#else
        SnakeCaseNamingPolicy.Instance;

#endif

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public static SnakeCaseNamingPolicy Instance { get; } = new();

        public override string ConvertName(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>用 snake_case 固定策略的 STJ 适配器，证明自定义序列化器会绕开 config 里的 options。</summary>
    private sealed class SnakeCaseSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = SnakeCase
        };

        public static SnakeCaseSerializer Instance { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string json, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(json, type, Options);
    }

    private static string Body_SerializationAndTransform(TestRequest request) => Encoding.UTF8.GetString(request.Body);
}