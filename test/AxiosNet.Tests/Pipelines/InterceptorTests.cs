
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 拦截器：请求/响应双向、异步与顺序、抛错与返回 null、替换配置与响应。
/// </summary>
public sealed class InterceptorTests
{
    // ===============================================================
    // Bug 9：请求拦截器返回 null Task 漏出裸 NRE
    // ===============================================================

    [Fact]
    public async Task RequestInterceptor_ReturningNullTask_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Interceptors.Request.Use((Func<AxiosConfig, Task<AxiosConfig>>)(_ => null!));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Contains("null Task", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RequestInterceptor_OnRejectedReturningNullTask_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // OnFulfilled 先抛，再让 OnRejected 返回 null：两条路径都要给出可定位的配置错误而不是裸 NRE
        client.Interceptors.Request.Use(
            (Func<AxiosConfig, Task<AxiosConfig>>)(_ => throw new InvalidOperationException("boom")),
            (Func<Exception, Task<AxiosConfig>>)(_ => null!));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Contains("null Task", ex.Message);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 自动切换后移 + transform 去重语义
    // ------------------------------------------------------------------

    [Fact]
    public async Task Interceptor_ReplacingConfig_KeepsAutoStreamSwitch()
    {
        var handler = new RecordingHandler(_ => StreamResponse());
        using var client = new AxiosClient(handler: handler);

        // 拦截器整体替换 config：新 config 的 responseType 是默认 Json 且未显式设置过，
        // 泛型 <Stream> 的自动切换必须仍然生效（切换点已在拦截器之后）。
        client.Interceptors.Request.Use(_ => Task.FromResult(new AxiosConfig { Url = "http://localhost/stream" }));

        var response = await client.GetAsync<Stream>("http://localhost/other");
        Assert.IsAssignableFrom<Stream>(response.Data);
    }

    // ---------------------------------------------------------------
    // 拦截器：OnRejected 可以从网络层错误恢复（不只是错误状态码）
    // ---------------------------------------------------------------

    [Fact]
    public async Task ResponseInterceptor_RejectedCanRecoverFromNetworkError()
    {
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(
            onFulfilled: (AxiosResponse response) => response,
            onRejected: (Exception ex) =>
            {
                var error = Assert.IsType<AxiosException>(ex);
                Assert.Equal(AxiosException.ErrNetwork, error.Code);
                Assert.Null(error.Response);
                return new AxiosResponse
                {
                    Status = 200,
                    StatusText = "Recovered",
                    Data = "fallback"
                };
            });

        var response = await client.GetAsync(
            "http://127.0.0.1:1/unreachable",
            c => c.ResponseType = ResponseType.Text);

        Assert.Equal(200, response.Status);
        Assert.Equal("fallback", Assert.IsType<string>(response.Data));
    }

    // ------------------------------------------------------------------
    // 拦截器返回 null
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestInterceptor_ReturningNull_ThrowsBadOptionValue_NotNullReference()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosConfig, Task<AxiosConfig>> returnsNull = _ => Task.FromResult<AxiosConfig>(null!);
        client.Interceptors.Request.Use(returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("请求拦截器", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RequestInterceptorRejected_ReturningNull_AlsoThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosConfig, Task<AxiosConfig>> boom = _ => throw new InvalidOperationException("boom");
        Func<Exception, Task<AxiosConfig>> returnsNull = _ => Task.FromResult<AxiosConfig>(null!);
        client.Interceptors.Request.Use(boom, returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptor_ReturningNull_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("响应拦截器", ex.Message);
    }

    [Fact]
    public async Task ResponseInterceptorRejected_ReturningNull_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = true }, statusCode: 500));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> identity = response => Task.FromResult(response);
        Func<Exception, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(identity, returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task TypedRequest_WithNullReturningResponseInterceptor_DoesNotThrowNullReference()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(returnsNull);

        // 之前 GetAsync<T> 会在 AsTyped 上抛裸 NullReferenceException
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<JsonElement>(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task RequestInterceptor_CanMutateConfig()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Trace", "abc-123");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal("abc-123", response.Data.GetProperty("trace").GetString());
    }

    [Fact]
    public async Task RequestInterceptors_RunInReverseRegistrationOrder()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { order = req.Headers.GetValueOrDefault("x-order") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Order", config.Headers.Get("X-Order") + "->A");
            return config;
        });
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Order", config.Headers.Get("X-Order") + "->B");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);

        // 后注册的 B 先执行，因此顺序为 B -> A
        Assert.Equal("->B->A", response.Data.GetProperty("order").GetString());
    }

    [Fact]
    public async Task RequestInterceptor_Eject_RemovesInterceptor()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        var id = client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Trace", "present");
            return config;
        });
        Assert.Equal(1, client.Interceptors.Request.Count);

        Assert.True(client.Interceptors.Request.Eject(id));
        Assert.Equal(0, client.Interceptors.Request.Count);

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Null(response.Data.GetProperty("trace").GetString());
        Assert.False(server.Received[0].Headers.ContainsKey("x-trace"));
    }

    [Fact]
    public async Task AsyncRequestInterceptor_IsAwaited()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { trace = req.Headers.GetValueOrDefault("x-trace") }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(async config =>
        {
            await Task.Delay(20);
            config.Headers.Set("X-Trace", "async");
            return config;
        });

        var response = await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal("async", response.Data.GetProperty("trace").GetString());
    }

    [Fact]
    public async Task ResponseInterceptor_TransformsResponseInRegistrationOrder()
    {
        await using var server = TestHttpServer.Start(_
            => new TestResponse(Body: Encoding.UTF8.GetBytes("hello"), ContentType: "text/plain"));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(response =>
        {
            response.Data = ((string)response.Data!).ToUpperInvariant();
            return response;
        });
        client.Interceptors.Response.Use(response =>
        {
            response.Data = (string)response.Data! + "!";
            return response;
        });

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.Equal("HELLO!", response.Data);
    }

    [Fact]
    public async Task ResponseInterceptor_RejectedCanRecoverFromErrorStatus()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404, Body: TestJson.Bytes(new { error = "missing" })));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use(
            onFulfilled: (AxiosResponse response) => response,
            onRejected: (Exception ex) =>
            {
                var error = Assert.IsType<AxiosException>(ex);
                Assert.Equal(404, error.Response!.Status);
                return new AxiosResponse
                {
                    Status = 200,
                    StatusText = "Recovered",
                    Data = "fallback"
                };
            });

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal(200, response.Status);
        Assert.Equal("fallback", Assert.IsType<string>(response.Data));
    }

    [Fact]
    public async Task RequestInterceptor_Throwing_PropagatesAndSkipsHttpCall()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(new Func<AxiosConfig, AxiosConfig>(
            _ => throw new InvalidOperationException("blocked")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync(server.Url));
        Assert.Equal("blocked", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptor_FulfilledSeesTypedAgnosticResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { value = 42 }));
        using var client = new AxiosClient();

        var seenStatus = 0;
        client.Interceptors.Response.Use(response =>
        {
            seenStatus = response.Status;
            return response;
        });

        await client.GetAsync<JsonElement>(server.Url);
        Assert.Equal(200, seenStatus);
    }

    // ---------------------------------------------------------------
    // 快照缓存：无变更时复用，但 Use / Eject / Clear 必须立即可见
    // （回归：缓存若不在这些操作上失效，运行期增删拦截器会静默不生效）
    // ---------------------------------------------------------------

    [Fact]
    public async Task InterceptorSnapshotCache_InvalidatedOnEjectAndClear()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Response(new { first = req.Headers.GetValueOrDefault("x-first"), second = req.Headers.GetValueOrDefault("x-second") }));
        using var client = new AxiosClient();

        var trace = new List<string>();
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-First", "1");
            trace.Add("first");
            return config;
        });
        var secondId = client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Second", "1");
            trace.Add("second");
            return config;
        });

        // 第一次请求建立快照缓存
        await client.GetAsync(server.Url);
        Assert.Equal(2, trace.Count);

        // Eject 后：缓存必须失效，被弹出的拦截器不再执行
        client.Interceptors.Request.Eject(secondId);
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Equal(new[] { "first" }, trace);

        // Use 后：缓存必须失效，新注册的拦截器立即生效（按注册逆序执行）
        client.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Second", "2");
            trace.Add("second-v2");
            return config;
        });
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Equal(new[] { "second-v2", "first" }, trace);

        // Clear 后：全部不再执行
        client.Interceptors.Request.Clear();
        trace.Clear();
        await client.GetAsync(server.Url);
        Assert.Empty(trace);
    }

    /// <summary>
    /// 泛型请求的响应对象由管线就地填充（<c>SendTypedAsync&lt;T&gt;</c> 预先造好），
    /// 但拦截器把对象换掉时必须仍按 T 转换 —— 那时拿到的是一份全新的非泛型响应。
    /// </summary>
    [Fact]
    public async Task ResponseInterceptor_ReplacingResponse_StillConvertsDataForGenericRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 1 }));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use((AxiosResponse response) => new AxiosResponse
        {
            Data = new Profile { Id = 42 },
            Status = response.Status,
            StatusText = response.StatusText,
            Headers = response.Headers
        });

        var response = await client.GetAsync<Profile>(server.Url);
        Assert.Equal(42, response.Data!.Id);
    }

    /// <summary>
    /// 拦截器在泛型请求的响应上写入了与 T 不匹配的 Data：必须报 <c>ERR_BAD_RESPONSE</c>，
    /// 不能把检查推迟到 <c>Data</c> getter（那是裸 <see cref="InvalidCastException"/>）。
    /// </summary>
    [Fact]
    public async Task ResponseInterceptor_WritingIncompatibleData_FailsWithBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { id = 1 }));
        using var client = new AxiosClient();

        client.Interceptors.Response.Use((AxiosResponse response) =>
        {
            response.Data = "not a profile";
            return response;
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Profile>(server.Url));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    /// <summary>
    /// 限制类配置的范围校验必须针对**拦截器之后**的生效配置：在校验之后才允许拦截器改写，
    /// 就等于允许「拦截器写 Timeout = -1」把负值带进管线，而 <c>hasTimeout</c> 随后为 false，
    /// 静默变成「不超时」——与文档承诺的 fail-fast 相反。
    /// </summary>
    [Fact]
    public async Task RequestInterceptor_NegativeTimeout_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Timeout = TimeSpan.FromSeconds(-1);
            return config;
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    /// <summary>
    /// 校验只针对最终配置，所以拦截器也可以修正调用方给的非法值（而不是被前置校验提前打死）。
    /// </summary>
    [Fact]
    public async Task RequestInterceptor_MayRepairInvalidValueFromCaller()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient();

        client.Interceptors.Request.Use(config =>
        {
            config.Timeout = TimeSpan.FromSeconds(30);
            return config;
        });

        await client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromSeconds(-1));

        Assert.Single(server.Received);
    }

    // ---------------------------------------------------------------
    // 请求拦截器的 rejected 语义（与 axios 的有意分歧）
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestInterceptor_OnRejected_DoesNotSwallowSendStageErrors()
    {
        using var client = new AxiosClient();
        var rejected = false;

        client.Interceptors.Request.Use(
            config => Task.FromResult(config),
            ex =>
            {
                rejected = true;
                return Task.FromResult(new AxiosConfig());
            });

        // 未被监听的端口：错误发生在发送阶段，绝不能进请求拦截器的 rejected
        // （axios 里这会变成「拿返回的 config 再发一次请求」，只做日志的 rejected 会静默重发 POST）
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/unreachable", c => c.Timeout = TimeSpan.FromSeconds(5)));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.False(rejected);
    }

    [Fact]
    public async Task RequestInterceptor_OnRejected_RecoversOnlyItsOwnOnFulfilledFailure()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        var recovered = false;

        client.Interceptors.Request.Use(
            _ => throw new InvalidOperationException("token expired"),
            ex =>
            {
                Assert.IsType<InvalidOperationException>(ex);
                recovered = true;
                return Task.FromResult(new AxiosConfig { Url = server.Url + "retry" });
            });

        var response = await client.GetAsync(server.Url + "original");

        Assert.Equal(200, response.Status);
        Assert.True(recovered);

        // 原始请求一次都没发出去，rejected 返回的 config 被当成「再发一次」
        var sent = Assert.Single(server.Received);
        Assert.Equal("/retry", sent.Path);
        Assert.Equal("GET", sent.Method);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------


    private static HttpResponseMessage StreamResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(new MemoryStream(TestJson.Bytes(new Person("alice", 1))));
        return response;
    }


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


    private sealed class Profile
    {
        public int Id { get; set; }
    }


    /// <summary>Dispose 时阻塞到手动放行的 Handler，用于验证「释放不在锁内执行」。</summary>
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


    /// <summary>记录 Handler 创建/释放次数：用于断言驱逐与关闭「不漏释放、不重复释放」。</summary>
    private sealed class HandlerLedger
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public HttpMessageHandler Create()
        {
            Interlocked.Increment(ref _created);
            return new LedgerHandler(this);
        }

        private sealed class LedgerHandler : HttpMessageHandler
        {
            private readonly HandlerLedger _owner;
            private int _disposed;

            public LedgerHandler(HandlerLedger owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                base.Dispose(disposing);
            }
        }
    }
}