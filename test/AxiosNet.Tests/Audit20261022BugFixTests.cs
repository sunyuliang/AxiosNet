using System.Collections.Immutable;
using System.Net;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 全库审查确认的 6 个 bug 的回归测试（每个发现都有动态复现支撑）：
/// <list type="bullet">
/// <item>Bug 1：回放 <c>response.Config</c> 时实例默认 transform 链重复执行（MergeConfig 拼接不去重）；</item>
/// <item>Bug 2：表单 / multipart / params 展平无环检测，自引用对象图栈溢出杀进程（JSON 路径有 STJ 环检测，这条自研路径没有）；</item>
/// <item>Bug 3：<c>HttpConnectionPool.Dispose</c> 持锁释放 Handler（同文件不变式要求锁外释放，EvictIdle 已遵守）；</item>
/// <item>Bug 4：管线自动切换 ResponseType 时置了 <c>ResponseTypeSet</c>，回放时被当成显式意图污染后续请求；</item>
/// <item>Bug 5：只实现 <c>IReadOnlyDictionary</c> 的字典被当 POCO 反射成 <c>[Key]</c>/<c>[Value]</c> 字段；</item>
/// <item>Bug 6：<c>EvictionInterval</c> 超过 Timer 上限（约 49.7 天）时构造期漏出裸 <c>ArgumentOutOfRangeException</c>。</item>
/// </list>
/// </summary>
public sealed class Audit20261022BugFixTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // ===============================================================
    // Bug 1：回放 response.Config → 实例默认 transform 链重复执行
    // ===============================================================

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

        // 回放一次：Defaults 链只应再执行一次（此前每次回放多执行一遍）
        await client.RequestAsync(first.Config!);
        Assert.Equal(2, instanceCalls);

        // 连续回放不再累积
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

        // 目标实例默认链执行一次（既有回放契约）
        Assert.Equal(1, targetDefaultCalls);
        // 源实例的链（默认 + 按请求）作为「按请求项」各执行一次：回放是一次新请求，
        // 关键是不重复执行（修复前同一实例回放会把默认链再叠一遍，见上面的计数用例）
        Assert.Equal(2, perRequestCalls);
        Assert.Equal(2, sourceDefaultCalls);
    }

    [Fact]
    public async Task PerRequestTransform_StillAppendedToInstanceChain()
    {
        // 普通按请求配置（链里不含 Defaults）的合并语义不变：实例默认在前、本次追加在后
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
        // 回放前在 configure 里整链替换：自己的 transform 只执行一次，且不炸
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

    // ===============================================================
    // Bug 2：表单 / multipart / params 展平无环检测
    // ===============================================================

    private sealed class Node
    {
        public string? Name { get; set; }
        public List<Node> Children { get; } = new();
    }

    private sealed class Pair
    {
        public Leaf? Left { get; set; }
        public Leaf? Right { get; set; }
    }

    private sealed class Leaf
    {
        public string? Value { get; set; }
    }

    [Fact]
    public async Task FormUrlEncoded_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "up", node, c => c.ContentType = "application/x-www-form-urlencoded"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("循环引用", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task FormUrlEncoded_MutualReference_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var a = new Node { Name = "a" };
        var b = new Node { Name = "b" };
        a.Children.Add(b);
        b.Children.Add(a);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "up", a, c => c.ContentType = "application/x-www-form-urlencoded"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostFormAsync(server.Url + "up", node));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Params_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "q", c => c.Params = node));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task FormUrlEncoded_SharedChildObject_IsNotTreatedAsCycle()
    {
        // 同一个子对象出现在两个分支（DAG 而非环）：路径式环检测必须放行，展平两次
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var shared = new Leaf { Value = "v" };
        var root = new Pair { Left = shared, Right = shared };

        await client.PostAsync(server.Url + "up", root, c => c.ContentType = "application/x-www-form-urlencoded");

        // 表单字段名用属性名原样（ResolveFieldName 只认 [JsonPropertyName] 与属性名）
        Assert.Equal("Left[Value]=v&Right[Value]=v", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_DeeplyNestedGraph_StillFlattens()
    {
        // 合法深层嵌套（10 层）不受环检测影响
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var root = new Node { Name = "0" };
        var current = root;
        for (var i = 1; i <= 10; i++)
        {
            var next = new Node { Name = i.ToString() };
            current.Children.Add(next);
            current = next;
        }

        await client.PostAsync(server.Url + "up", root, c => c.ContentType = "application/x-www-form-urlencoded");

        // 嵌套数组按既有约定用 a[] 形式（不是 a[0]）
        Assert.Contains("Children[][Name]=1", Body(server.Received[0]));
    }

    [Fact]
    public async Task Params_SharedChildObject_IsNotTreatedAsCycle()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var shared = new Leaf { Value = "v" };
        var root = new Pair { Left = shared, Right = shared };

        await client.GetAsync("q", c => c.Params = root);

        // 键名的方括号按既有约定不转义
        Assert.Contains("Left[Value]=v", server.Received[0].RawUrl);
        Assert.Contains("Right[Value]=v", server.Received[0].RawUrl);
    }

    // ===============================================================
    // Bug 3：HttpConnectionPool.Dispose 持锁释放 Handler
    // ===============================================================

    [Fact]
    public async Task Pool_Dispose_ReleasesHandlerOutsideLock_StatisticsNotBlocked()
    {
        var disposeEntered = new ManualResetEventSlim(false);
        var releaseDispose = new ManualResetEventSlim(false);
        var handler = new BlockingDisposeHandler(disposeEntered, releaseDispose);

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            HandlerFactory = _ => handler,
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        using (pool.Lease(new Uri("http://127.0.0.1:9500/x")))
        {
        }

        var disposeTask = Task.Run(() => pool.Dispose());
        var statsTask = Task.Run(() => pool.GetStatistics());

        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)), "Dispose 没有进入 Handler 释放。");

        // 修复前：Dispose 持着 endpoint.SyncRoot 等 Handler 关闭，统计被一起拖住。
        // 契约（同文件 EvictIdle 的不变式）：锁内只做判定，释放移到锁外。
        var statsCompleted = await Task.WhenAny(statsTask, Task.Delay(TimeSpan.FromSeconds(5))) == statsTask;
        Assert.True(statsCompleted, "Dispose 持锁释放 Handler：GetStatistics 被拖住（应锁内判定、锁外释放）。");

        releaseDispose.Set();
        var disposeCompleted = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))) == disposeTask;
        Assert.True(disposeCompleted, "Dispose 未在放行后完成。");
        Assert.True(handler.Disposed);
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

    // ===============================================================
    // Bug 4：ResponseType 自动改写随回放污染
    // ===============================================================

    [Fact]
    public async Task ReplayObjectTypedConfig_DoesNotLeakDynamicResponseType()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 7)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // <object>：管线自动切 Dynamic
        var r1 = await client.RequestAsync<object>(new AxiosConfig { Url = "x" });
        Assert.Equal(ResponseType.Dynamic, r1.Config!.ResponseType);

        // 换泛型参数回放：必须能正常反序列化成 Person（修复前带回 Dynamic → ERR_BAD_RESPONSE）
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

        // 同样按 <object> 回放：动态展开语义不变
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

        // 修复前：Stream 被带回，Data 是 ResponseStreamOwner → ERR_BAD_RESPONSE
        var r2 = await client.GetAsync<Person>("x", r1.Config!);
        Assert.NotNull(r2.Data);
        Assert.Equal("n", r2.Data!.Name);
    }

    [Fact]
    public async Task ExplicitResponseType_SurvivesReplay()
    {
        // 调用方显式设置的 responseType 是真实意图，回放必须保留
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var r1 = await client.GetAsync("x", c => c.ResponseType = ResponseType.Text);
        Assert.Equal(""""{"name":"n","age":1}"""", r1.Data);

        var r2 = await client.RequestAsync(r1.Config!);
        Assert.Equal(ResponseType.Text, r2.Config!.ResponseType);
    }

    // ===============================================================
    // Bug 5：IReadOnlyDictionary 被展平成 Key/Value
    // ===============================================================

    private sealed class ReadOnlyStringMap : IReadOnlyDictionary<string, object>
    {
        private readonly Dictionary<string, object> _inner = new();

        public object this[string key] => _inner[key];
        public IEnumerable<string> Keys => _inner.Keys;
        public IEnumerable<object> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
        public bool TryGetValue(string key, out object value) => _inner.TryGetValue(key, out value!);
        public void Add(string key, object value) => _inner[key] = value;
    }

    [Fact]
    public async Task ReadOnlyDictionary_FormUrlEncoded_FlattensAsKeyValuePairs()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = new ReadOnlyStringMap { { "a", 1 }, { "b", "x" } };

        await client.PostAsync(server.Url + "up", data, c => c.ContentType = "application/x-www-form-urlencoded");

        // 修复前：KeyValuePair 被当 POCO 反射 → a[Key]=a&a[Value]=1&b[Key]=b&b[Value]=x
        Assert.Equal("a=1&b=x", Body(server.Received[0]));
    }

    [Fact]
    public async Task ReadOnlyDictionary_Multipart_FlattensAsNamedFields()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = new ReadOnlyStringMap { { "a", 1 } };

        await client.PostFormAsync(server.Url + "up", data);

        // 简单 token 的 disposition 不带引号（.NET MultipartFormDataContent 的行为）
        Assert.Contains("name=a", Body(server.Received[0]));
        Assert.DoesNotContain("a[Key]", Body(server.Received[0]));
    }

    [Fact]
    public async Task ImmutableDictionary_FormUrlEncoded_StillHitsGenericDictionaryBranch()
    {
        // 回归：同时实现 IDictionary<string,object> 与 IReadOnlyDictionary 的既有行为不变
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = ImmutableDictionary.CreateRange(
            new[] { new KeyValuePair<string, object>("a", 1) });

        await client.PostAsync(server.Url + "up", data, c => c.ContentType = "application/x-www-form-urlencoded");

        Assert.Equal("a=1", Body(server.Received[0]));
    }

    // ===============================================================
    // Bug 6：EvictionInterval 越界裸异常
    // ===============================================================

    [Fact]
    public void EvictionInterval_BeyondTimerLimit_ThrowsAxiosException()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromDays(50) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void EvictionInterval_Negative_ThrowsAxiosException()
    {
        var ex = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromSeconds(-1) }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void EvictionInterval_Zero_DoesNotThrow()
    {
        // 0 = 不启用后台定时器（文档化行为），且 IdleHandlerLifetime 默认 > 0
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.Zero });
        Assert.NotNull(pool);
    }

    [Fact]
    public void EvictionInterval_JustUnderTimerLimit_DoesNotThrow()
    {
        // 49 天 < 约 49.7 天的 Timer 上限：合法
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions { EvictionInterval = TimeSpan.FromDays(49) });
        Assert.NotNull(pool);
    }
}
