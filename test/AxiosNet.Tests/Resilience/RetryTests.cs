
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 重试：状态码/网络错误分类、指数退避与抖动、`Retry-After`、幂等门禁、正文重放。
/// </summary>
public sealed class RetryTests
{
    // ------------------------------------------------------------------
    // 默认行为：不重试
    // ------------------------------------------------------------------

    [Fact]
    public async Task Default_DoesNotRetry_OnServerError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(500, ex.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PolicyWithZeroRetries_DoesNotRetry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 503));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(0) } });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // 内置策略
    // ------------------------------------------------------------------

    [Fact]
    public async Task ServerError_IsRetriedUntilSuccess()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task ServerError_StopsAtMaxRetries_AndThrowsLastError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        // 1 次首发 + 2 次重试
        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 404));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Single(server.Received);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(502)]
    public async Task TransientStatusCodes_AreRetried(int status)
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "retry me" }, statusCode: status)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Post_IsNotRetriedByDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } });

        await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "x", new { id = 1 }));

        // 非幂等方法：服务端可能已经执行过，默认不重发
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Post_IsRetried_WhenNonIdempotentOptIn()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });

        var response = await client.PostAsync(server.Url + "x", new { id = 1 });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task NetworkError_IsRetried()
    {
        using var handler = new ScriptedHandler(failures: 1);
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync(
            "http://retry.local/x",
            c => c.Retry.Policy = Immediate(2));

        Assert.Equal(200, response.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Timeout_IsRetried()
    {
        using var handler = new ScriptedHandler(failures: 0, delayFirstCall: TimeSpan.FromSeconds(5));
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync("http://retry.local/x", c =>
        {
            c.Timeout = TimeSpan.FromMilliseconds(100);
            c.Retry.Policy = Immediate(2);
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, handler.Calls);
    }

    // ------------------------------------------------------------------
    // 间隔 / 退避 / Retry-After
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delay_IsAppliedBetweenAttempts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(120)) }
        });

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));
        stopwatch.Stop();

        // 两次重试 × 120ms；留足调度余量，只断言「确实等过」
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(180),
            $"总耗时 {stopwatch.ElapsedMilliseconds}ms，未体现 2 × 120ms 的重试间隔");
        Assert.Equal(3, server.Received.Count);
    }


    // ------------------------------------------------------------------
    // 请求体重放
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestBody_IsResentOnRetry()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        // POST 默认不重试（非幂等）：这里要验证的是「正文被完整重放」，显式打开
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });

        await client.PostAsync(server.Url + "x", new { name = "bob" });

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(server.Received[0].Body, server.Received[1].Body);
        Assert.Contains("bob", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task SeekableStreamBody_IsRewoundBeforeRetry()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("streamed-payload"));

        await client.PostAsync(server.Url + "x", body, c => c.ContentType = "text/plain");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("streamed-payload", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("streamed-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task NonSeekableStreamBody_WithRetry_FailsFast()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });
        using var body = new NonSeekableStream(Encoding.UTF8.GetBytes("streamed-payload"));

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", body, c => c.ContentType = "text/plain"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task StreamClosedBeforeRetry_FailsWithBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        MemoryStream? body = new MemoryStream(Encoding.UTF8.GetBytes("streamed-payload"));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true },

                // 模拟「调用方在两次尝试之间把流关了」：正文从此不可重放
                OnRetry = _ => body.Dispose()
            }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", body, c => c.ContentType = "text/plain"));

        // 拨不回起点就是「这次重试做不了」：统一错误模型，不漏裸 ObjectDisposedException
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.IsType<ObjectDisposedException>(ex.Cause);
        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // 回调 / 自定义策略 / 作用域
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnRetry_ReportsAttemptsDelaysAndResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var observed = new List<AxiosRetryContext>();
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = RetryPolicy.Constant(2, TimeSpan.FromMilliseconds(20)),
                OnRetry = observed.Add
            }
        });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(2, observed.Count);
        Assert.Equal(1, observed[0].Attempt);
        Assert.Equal(2, observed[1].Attempt);
        Assert.Equal(2, observed[0].MaxRetries);
        Assert.Equal(500, observed[0].StatusCode);
        Assert.Equal(AxiosException.ErrBadResponse, observed[0].ErrorCode);
        Assert.Equal(TimeSpan.FromMilliseconds(20), observed[0].Delay);
    }

    [Fact]
    public async Task StatusCode_IsSnapshot_NotRewrittenByLaterAttempts()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { ok = true }, statusCode: 503)
            : TestJson.Response(new { ok = true }));

        var observed = new List<AxiosRetryContext>();
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = Immediate(1), OnRetry = observed.Add }
        });

        // 必须走泛型入口：它把同一个响应对象交给后续尝试就地填充，才存在「回调外读到下一次的值」的问题
        var response = await client.GetAsync<Dictionary<string, object>>(server.Url + "x");
        Assert.Equal(200, response.Status);

        // 回调结束后 Response 已被第 2 次尝试改写成 200，但 StatusCode 是构造时的快照，仍记着「那一次失败是 503」。
        Assert.Equal(503, observed[0].StatusCode);
        Assert.Equal(200, observed[0].Response!.Status);
    }

    [Fact]
    public async Task OnRetry_Throwing_AbortsRequest_WithoutTreatingItAsFailure()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = Immediate(3),
                OnRetry = _ => throw new InvalidOperationException("回调写错了")
            }
        });

        // 回调是调用方代码：它抛出的异常原样抛出，不该被当成「本次尝试失败」再重试三次。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal("回调写错了", ex.Message);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CustomPolicy_IsHonored()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = new OnlyOddAttemptsPolicy() } });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        // 自定义策略只在第 1、2 次失败后重试（第 3 次失败就放弃），这里第 3 次成功
        Assert.Equal(3, server.Received.Count);
    }

    [Fact]
    public async Task PerRequest_CanDisableInstanceDefaultRetry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } });

        await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Retry.Policy = null));

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequest_CanEnableRetry_OnInstanceWithoutPolicy()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.Retry.Policy = Immediate(1));

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task Cancellation_DuringRetry_StopsRetrying()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        using var cts = new CancellationTokenSource();
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = Immediate(3),
                OnRetry = _ => cts.Cancel()
            }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.CancellationToken = cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptor_RunsOnce_ForAllAttempts()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } });

        var interceptorCalls = 0;
        client.Interceptors.Response.Use(response =>
        {
            interceptorCalls++;
            return Task.FromResult(response);
        });

        await client.GetAsync(server.Url + "x");

        // 3 次尝试，拦截器只看到最终结果
        Assert.Equal(3, server.Received.Count);
        Assert.Equal(1, interceptorCalls);
    }

    [Fact]
    public async Task NegativeMaxRetries_ThrowsBadOption()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(-1) } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 策略行为（幂等判定 / 附加条件）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Put_IsRetriedByDefault()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        var response = await client.PutAsync(server.Url + "x", new { id = 1 });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task RetryWhen_GateCanVeto()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        // 只对 429 重试：500 虽属默认可重试范围，但附加条件否决
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                Policy = new RetryPolicy(3, TimeSpan.Zero) { RetryWhen = context => context.StatusCode == 429 }
            }
        });

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Single(server.Received);
    }

    /// <summary>测试用的「零等待」策略：只验证重试语义，不让用例真的等。</summary>
    private static RetryPolicy Immediate(int maxRetries) => new(maxRetries, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>只在第 1、2 次失败后重试的自定义策略（验证接口本身可插拔）。</summary>
    private sealed class OnlyOddAttemptsPolicy : IAxiosRetryPolicy
    {
        public int MaxRetries => 5;

        public bool ShouldRetry(AxiosRetryContext context) => context.Attempt <= 2;

        public TimeSpan GetDelay(AxiosRetryContext context) => TimeSpan.Zero;
    }

    /// <summary>按脚本失败的自定义 Handler：用于构造网络错误与超时，不依赖真实端口。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly int _failures;
        private readonly TimeSpan _delayFirstCall;
        private int _calls;

        public ScriptedHandler(int failures, TimeSpan? delayFirstCall = null)
        {
            _failures = failures;
            _delayFirstCall = delayFirstCall ?? TimeSpan.Zero;
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            if (call == 1 && _delayFirstCall > TimeSpan.Zero)
            {
                await Task.Delay(_delayFirstCall, cancellationToken).ConfigureAwait(false);
            }

            if (call <= _failures)
            {
                throw new HttpRequestException("simulated network failure");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>不可 seek 的流（模拟网络流 / 管道流）。</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] content) => _inner = new MemoryStream(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}