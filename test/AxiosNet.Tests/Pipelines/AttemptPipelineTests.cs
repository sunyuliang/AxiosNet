using System.Collections.Concurrent;
using System.Text;
using AxiosNet.Circuit;
using AxiosNet.Pipeline;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Requests;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 尝试管线：内置顺序（重试 → 熔断 → 闸门 → 终端）、自定义中间件的调用次数与位置、
/// 编排校验（不提供「放最外层」的入口）、并发槽位的交接，以及请求头的读取。
/// </summary>
/// <remarks>槽位归还那几条要用队列超时来证明「额度确实回来了」，属时序敏感，故整类进 <see cref="TimingCollection"/>。</remarks>
[Collection(TimingCollection.Name)]
public sealed class AttemptPipelineTests
{
    private static RetryPolicy Immediate(int maxRetries) => new(maxRetries, TimeSpan.Zero);

    /// <summary>探针中间件：记下名字与本次的尝试序号，然后原样交给下一环。</summary>
    private static AxiosAttemptMiddleware Probe(ConcurrentQueue<string> log, string name)
        => next => async (request, ctx) =>
        {
            log.Enqueue($"{name}#{ctx.AttemptNumber}");
            return await next(request, ctx);
        };

    // ------------------------------------------------------------------
    // 默认链
    // ------------------------------------------------------------------

    [Fact]
    public void Default_IsRetryThenCircuitThenGate()
    {
        Assert.Equal(3, AxiosAttemptPipeline.Default.Middleware.Count);
        Assert.Same(Attempt.Retry, AxiosAttemptPipeline.Default.Middleware[0]);
        Assert.Same(Attempt.Circuit, AxiosAttemptPipeline.Default.Middleware[1]);
        Assert.Same(Attempt.Gate, AxiosAttemptPipeline.Default.Middleware[2]);
    }

    [Fact]
    public async Task Default_DoesNotRetry_WithoutPolicy()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var log = new ConcurrentQueue<string>();
        using var client = new AxiosClient(pipeline: AxiosAttemptPipeline.Default.Append(Probe(log, "inner")));

        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 没配重试策略：只有一次尝试，探针也就只跑一次（重试中间件此时是纯直通）
        Assert.Equal(new[] { "inner#1" }, log);
        Assert.Single(server.Received);
    }

    // ------------------------------------------------------------------
    // 位置决定调用次数
    // ------------------------------------------------------------------

    [Fact]
    public void Axios_Create_AppliesPipeline()
    {
        var pipeline = AxiosAttemptPipeline.Default.Append(Probe(new ConcurrentQueue<string>(), "x"));

        using var client = Axios.Create(pipeline: pipeline);
        Assert.Same(pipeline, client.AttemptPipeline);

        using var configured = Axios.Create(c => c.Timeout = TimeSpan.FromSeconds(5), pipeline: pipeline);
        Assert.Same(pipeline, configured.AttemptPipeline);
        Assert.Equal(TimeSpan.FromSeconds(5), configured.Defaults.Timeout);
    }

    [Fact]
    public async Task Append_RunsOncePerAttempt()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 503)
            : TestJson.Response(new { ok = true }));

        var log = new ConcurrentQueue<string>();
        using var client = new AxiosClient(
            new AxiosConfig { Retry = { Policy = Immediate(3) } },
            pipeline: AxiosAttemptPipeline.Default.Append(Probe(log, "inner")));

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Equal(3, server.Received.Count);

        // 落在重试之内：每次尝试各一次，尝试序号从 1 连续递增
        Assert.Equal(new[] { "inner#1", "inner#2", "inner#3" }, log);
    }

    [Fact]
    public async Task MiddlewareOutsideRetry_RunsOncePerRequest()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) < 3
            ? TestJson.Response(new { error = "boom" }, statusCode: 503)
            : TestJson.Response(new { ok = true }));

        var log = new ConcurrentQueue<string>();

        // 显式整体重排：把探针放到重试之前（Create 是唯一的「放最外层」入口）
        var pipeline = AxiosAttemptPipeline.Create(
            Probe(log, "outer"), Attempt.Retry, Attempt.Circuit, Attempt.Gate);

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(3) } }, pipeline: pipeline);

        await client.GetAsync(server.Url + "x");

        // 3 次尝试、但探针只跑一次 —— 这正是它与响应拦截器同构的原因
        Assert.Equal(new[] { "outer#1" }, log);
    }

    [Fact]
    public async Task Nesting_IsOuterToInner()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var log = new ConcurrentQueue<string>();
        var pipeline = AxiosAttemptPipeline.Create(
            Probe(log, "a"), Attempt.Retry, Probe(log, "b"), Attempt.Circuit, Probe(log, "c"), Attempt.Gate, Probe(log, "d"));

        using var client = new AxiosClient(pipeline: pipeline);

        await client.GetAsync(server.Url + "x");

        // 越靠前越外层：先被进入、后被返回（这里只看进入顺序）
        Assert.Equal(new[] { "a#1", "b#1", "c#1", "d#1" }, log);
    }

    // ------------------------------------------------------------------
    // 熔断排在闸门与一切内层之前
    // ------------------------------------------------------------------

    [Fact]
    public async Task CircuitRejection_SkipsEverythingInsideIt()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var log = new ConcurrentQueue<string>();
        using var client = new AxiosClient(
            new AxiosConfig { Circuit = { Policy = new AlwaysOpen() } },
            pipeline: AxiosAttemptPipeline.Default.Append(Probe(log, "inner")));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrCircuitOpen, ex.Code);

        // 熔断在最内层探针之外：请求没发出去，探针一次都不会跑，也就没占并发槽位
        Assert.Empty(log);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 并发槽位的交接
    // ------------------------------------------------------------------

    [Fact]
    public async Task Slot_IsHandedToTerminal_OnlyWhenLimitIsSet()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var without = new ConcurrentQueue<IDisposable?>();
        using (var client = new AxiosClient(pipeline: AxiosAttemptPipeline.Default.Append(SlotRecorder(without))))
        {
            await client.GetAsync(server.Url + "x");
        }

        var with = new ConcurrentQueue<IDisposable?>();
        using (var client = new AxiosClient(
                   new AxiosConfig { Limits = { MaxConcurrentRequests = 4 } },
                   pipeline: AxiosAttemptPipeline.Default.Append(SlotRecorder(with))))
        {
            await client.GetAsync(server.Url + "x");
        }

        Assert.Single(without);
        Assert.Null(without.First()); // 没设并发上限：闸门中间件不占位

        Assert.Single(with);
        Assert.NotNull(with.First()); // 设了上限：槽位经上下文交给终端，由终端归还
    }

    private static AxiosAttemptMiddleware SlotRecorder(ConcurrentQueue<IDisposable?> slots)
        => next => async (request, ctx) =>
        {
            slots.Enqueue(ctx.Slot);
            return await next(request, ctx);
        };

    /// <summary>槽位归还的回归：插在闸门之后的中间件如果不调用终端，终端就一次都不会执行 —— 归还的责任落回闸门。</summary>
    [Fact]
    public async Task Slot_IsReturned_WhenMiddlewareSkipsTerminal()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var pipeline = AxiosAttemptPipeline.Default.Append(_ => (request, ctx) =>
            new ValueTask<AxiosAttemptResult>(new AxiosAttemptResult(new AxiosResponse { Status = 204, Data = "stub" })));

        using var client = new AxiosClient(Limited(1), pipeline: pipeline);

        Assert.Equal(204, (await client.GetAsync(server.Url + "a")).Status);

        // 槽位若没归还，第二个请求会卡到队列超时
        Assert.Equal(204, (await client.GetAsync(server.Url + "b")).Status);
    }

    /// <summary>同上，但内层是抛异常而不是短路：异常路径同样要保证槽位回到池里。</summary>
    [Fact]
    public async Task Slot_IsReturned_WhenMiddlewareThrows()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // 只在第一次请求上抛：第二次要验证的是「还能不能拿到槽位」，不是异常本身
        var thrown = 0;
        var pipeline = AxiosAttemptPipeline.Default.Append(next => (request, ctx) =>
        {
            if (Interlocked.Increment(ref thrown) == 1)
            {
                throw new InvalidOperationException("middleware boom");
            }

            return next(request, ctx);
        });

        using var client = new AxiosClient(Limited(1), pipeline: pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(server.Url + "a"));

        Assert.Equal(200, (await client.GetAsync(server.Url + "b")).Status);
    }

    /// <summary>
    /// 反向的不变量：流式成功时槽位随正文流转交给 <c>StreamOwner</c>，
    /// 闸门若在那一刻归还，正文还在下载却已不占并发额度。
    /// </summary>
    [Fact]
    public async Task Slot_StaysWithTheStream_UntilItIsDisposed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(Limited(1));

        var streamed = await client.GetAsync(server.Url + "a", c => c.ResponseType = ResponseType.Stream);
        var body = (Stream)streamed.Data!;

        // 流还开着：额度仍被它占着
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "b"));
        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);

        body.Dispose();

        Assert.Equal(200, (await client.GetAsync(server.Url + "c")).Status);
    }

    /// <summary>
    /// 中间件换掉响应对象、把旧的 <c>StreamOwner</c> 带进新结果时，槽位必须继续跟着正文走：
    /// 否则正文还在下载，闸门却已经把额度还掉了。
    /// </summary>
    [Fact]
    public async Task ReplacingStreamResponse_KeepsSlotWithTheStream()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var pipeline = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
        {
            var attempt = await next(request, ctx);
            var replaced = new AxiosResponse { Status = attempt.Response.Status, Data = attempt.Response.Data };
            return new AxiosAttemptResult(replaced, attempt.StreamOwner);
        });

        using var client = new AxiosClient(Limited(1), pipeline: pipeline);

        var streamed = await client.GetAsync(server.Url + "a", c => c.ResponseType = ResponseType.Stream);
        var body = (Stream)streamed.Data!;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "b"));
        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);

        body.Dispose();

        Assert.Equal(200, (await client.GetAsync(server.Url + "c")).Status);
    }

    /// <summary>
    /// 中间件自建的 <c>StreamOwner</c> 不是库内凭证：闸门必须照常归还槽位 ——
    /// 否则「自建凭证 + 不调终端」会把一个并发额度永久留在外面。
    /// </summary>
    [Fact]
    public async Task SelfBuiltStreamOwner_DoesNotHoldTheGateSlot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var owner = new Tracker();
        var pipeline = AxiosAttemptPipeline.Default.Append(_ => (request, ctx) =>
            new ValueTask<AxiosAttemptResult>(new AxiosAttemptResult(
                new AxiosResponse { Status = 204, Data = owner },
                owner)));

        using var client = new AxiosClient(Limited(1), pipeline: pipeline);

        Assert.Equal(204, (await client.GetAsync(server.Url + "a")).Status);

        // 槽位若没归还，第二个请求会卡到队列超时
        Assert.Equal(204, (await client.GetAsync(server.Url + "b")).Status);

        // 自建凭证的释放归创建者：外层只在响应被换掉时兜底
        Assert.False(owner.Disposed);
    }

    /// <summary><c>WithAttemptToken</c> 只换本次尝试的预算令牌，调用方令牌不受影响（对冲 / 备用端点按分支设预算用）。</summary>
    [Fact]
    public async Task WithAttemptToken_OverridesOnlyTheAttemptBudget()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var branch = new CancellationTokenSource();

        CancellationToken insideAttempt = default;
        CancellationToken insideExternal = default;

        var pipeline = AxiosAttemptPipeline.Default.Append(next => (request, ctx) =>
        {
            insideExternal = ctx.ExternalToken;

            var derived = ctx.WithAttemptToken(branch.Token);
            insideAttempt = derived.AttemptToken;
            return next(request, derived);
        });

        using var client = new AxiosClient(pipeline: pipeline);
        await client.GetAsync(server.Url + "x");

        Assert.Equal(branch.Token, insideAttempt);
        Assert.Equal(CancellationToken.None, insideExternal);
        Assert.NotEqual(insideAttempt, insideExternal);

        // 原上下文没被改动：结构体按值传递，派生只影响自己的副本
        Assert.Equal(CancellationToken.None, insideExternal);
    }

    private static AxiosConfig Limited(int maxConcurrency) => new()
    {
        Limits = new() { MaxConcurrentRequests = maxConcurrency, QueueTimeout = TimeSpan.FromMilliseconds(300) }
    };

    // ------------------------------------------------------------------
    // 编排校验
    // ------------------------------------------------------------------

    [Fact]
    public void InsertBefore_FirstItem_IsRejected()
    {
        // 不提供「放到最外层」的入口：那个位置与响应拦截器同构，两个等价选项只会让人选错
        var ex = Assert.Throws<ArgumentException>(
            () => AxiosAttemptPipeline.Default.InsertBefore(Attempt.Retry, Probe(new ConcurrentQueue<string>(), "x")));

        Assert.Contains("响应拦截器", ex.Message);
    }

    [Fact]
    public void InsertAfter_IsAllowed()
    {
        var pipeline = AxiosAttemptPipeline.Default.InsertAfter(Attempt.Retry, Probe(new ConcurrentQueue<string>(), "x"));

        Assert.Equal(4, pipeline.Middleware.Count);
        Assert.Same(Attempt.Retry, pipeline.Middleware[0]);
        Assert.Same(Attempt.Circuit, pipeline.Middleware[2]);
    }

    [Fact]
    public void Create_RejectsDuplicateBuiltIns()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => AxiosAttemptPipeline.Create(Attempt.Retry, Attempt.Circuit, Attempt.Gate, Attempt.Gate));

        Assert.Contains("多次", ex.Message);
    }

    /// <summary>替换成另一个已在链上的内置项会造出嵌套重试 / 双闸门，必须和 Create 一样拦住。</summary>
    [Fact]
    public void Replace_RejectsDuplicateBuiltIns()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => AxiosAttemptPipeline.Default.Replace(Attempt.Circuit, Attempt.Retry));

        Assert.Contains("多次", ex.Message);

        // 换成非内置项是允许的
        var replaced = AxiosAttemptPipeline.Default.Replace(Attempt.Gate, Probe(new ConcurrentQueue<string>(), "x"));
        Assert.DoesNotContain(Attempt.Gate, replaced.Middleware);
    }

    [Fact]
    public void Remove_DropsTheMiddleware()
    {
        var pipeline = AxiosAttemptPipeline.Default.Remove(Attempt.Gate);

        Assert.Equal(2, pipeline.Middleware.Count);
        Assert.DoesNotContain(Attempt.Gate, pipeline.Middleware);

        // 原链不受影响：不可变，每次编辑都返回新实例
        Assert.Equal(3, AxiosAttemptPipeline.Default.Middleware.Count);
    }

    [Fact]
    public void IndexOf_RejectsUnknownAnchor()
    {
        Assert.Throws<ArgumentException>(
            () => AxiosAttemptPipeline.Default.Remove(Probe(new ConcurrentQueue<string>(), "x")));
    }

    // ------------------------------------------------------------------
    // 中间件可以短路（不调用 next 就没有请求）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Middleware_CanSendNothing()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var pipeline = AxiosAttemptPipeline.Default.Append(_ => (request, ctx) =>
            new ValueTask<AxiosAttemptResult>(new AxiosAttemptResult(new AxiosResponse { Status = 204, Data = "stub" })));

        using var client = new AxiosClient(pipeline: pipeline);

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(204, response.Status);
        Assert.Equal("stub", response.Data);
        Assert.Empty(server.Received);
    }

    /// <summary>默认结构体没有响应：库要报成可定位的配置错误，而不是让空引用在几层之外炸出来。</summary>
    [Fact]
    public async Task Middleware_ReturningDefaultResult_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var pipeline = AxiosAttemptPipeline.Default.Append(_ => (request, ctx) =>
            new ValueTask<AxiosAttemptResult>(default(AxiosAttemptResult)));

        using var client = new AxiosClient(pipeline: pipeline);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("default(AxiosAttemptResult)", ex.Message);
    }

    /// <summary>
    /// 熔断中间件读 <c>Status</c> 之前也要挡住 default 结果；这是编程错误，
    /// 连「无条件重试」的策略都不该被问一次（<c>ERR_BAD_OPTION_VALUE</c> 属库级不重试）。
    /// </summary>
    [Fact]
    public async Task DefaultResult_InsideCircuit_IsRejectedWithoutRetry()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var policy = new ForcedRetryPolicy();
        var pipeline = AxiosAttemptPipeline.Default.Append(_ => (request, ctx) =>
            new ValueTask<AxiosAttemptResult>(default(AxiosAttemptResult)));

        using var client = new AxiosClient(
            new AxiosConfig
            {
                Retry = { Policy = policy },
                Circuit = { Policy = new AlwaysAllowed() },
            },
            pipeline: pipeline);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Equal(0, policy.ShouldRetryCalls);
        Assert.Empty(server.Received);
    }

    /// <summary>中间件工厂返回 null 处理器属于编程错误：构造客户端时就报出来，别等到第一次请求。</summary>
    [Fact]
    public void MiddlewareFactory_ReturningNullHandler_IsRejected()
    {
        var pipeline = AxiosAttemptPipeline.Default.Append(_ => null!);

        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(pipeline: pipeline).Dispose());

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("null", ex.Message);
    }

    // ------------------------------------------------------------------
    // 请求头：响应侧能读到（短路没有请求消息）
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetRequestHeader_ReadsTheHeadersThatWereSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.Headers["X-Trace"] = "abc");

        Assert.Equal("abc", response.GetRequestHeader("X-Trace"));
        Assert.Equal("GET", response.Request!.Method.Method);

        // 请求头与响应头是两个集合，没发过的头查不到
        Assert.Null(response.GetRequestHeader("X-Not-Sent"));
    }

    [Fact]
    public async Task GetRequestHeader_IsNull_OnShortCircuit()
    {
        using var client = new AxiosClient();

        var response = await client.GetAsync<string>("http://127.0.0.1:1/never", c => c.ShortCircuit = new AxiosShortCircuit
        {
            Body = TestJson.Bytes(new { ok = true }),
            ContentType = "application/json; charset=utf-8"
        });

        // 短路没有底层请求消息：不伪造一个假的，直接给 null
        Assert.Null(response.Request);
        Assert.Null(response.GetRequestHeader("X-Trace"));
    }

    /// <summary>排队超时是库级强制不重试：连「无条件重试」的自定义策略都改不掉 —— 立刻重发只会再撞同一个满载的闸门。</summary>
    [Fact]
    public async Task QueueTimeout_IsNeverRetried_EvenByACustomPolicy()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });

        var policy = new ForcedRetryPolicy();

        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = policy },
            Limits = new() { MaxConcurrentRequests = 1, QueueTimeout = TimeSpan.FromMilliseconds(200) },
        });

        var holding = client.GetAsync(server.Url + "hold");
        await WaitUntilAsync(() => server.Received.Count >= 1, "第一条请求占住槽位");

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "queued"));

        // 库层面直接挡掉，策略一次都不被问 —— 要重试得由调用方自己在外层等并发降下来
        Assert.Equal(AxiosException.ErrQueueTimeout, ex.Code);
        Assert.Equal(0, policy.ShouldRetryCalls);

        release.TrySetResult(true);
        await holding;
    }

    /// <summary>
    /// 正文可重放性只在链上真有重试中间件时才要求：摘掉 Retry 之后，
    /// 不可 seek 的流也照常发送（检查放在重试中间件里而不是建上下文时的原因）。
    /// </summary>
    [Fact]
    public async Task RetryRemoved_DoesNotRequireReplayableBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(
            new AxiosConfig
            {
                Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
            },
            pipeline: AxiosAttemptPipeline.Default.Remove(Attempt.Retry));
        using var body = new NonSeekableStream(Encoding.UTF8.GetBytes("streamed-payload"));

        var response = await client.PostAsync(server.Url + "x", body, c => c.ContentType = "text/plain");

        // 只要能发出去就说明没做可重放性检查（否则会在第一次发送之前抛 ERR_BAD_OPTION_VALUE）
        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待超时：{what}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>无条件重试的自定义策略：用来证明队列超时的排除发生在策略之前。</summary>
    private sealed class ForcedRetryPolicy : IAxiosRetryPolicy
    {
        public int MaxRetries => 3;

        public int ShouldRetryCalls;

        public bool ShouldRetry(AxiosRetryContext context)
        {
            ShouldRetryCalls++;
            return true;
        }

        public TimeSpan GetDelay(AxiosRetryContext context) => TimeSpan.Zero;
    }

    /// <summary>永远拒绝的熔断策略：用来验证「拒绝发生在最内层之前」。</summary>
    private sealed class AlwaysOpen : IAxiosCircuitPolicy
    {
        public bool ShouldAllow(AxiosCircuitContext context) => false;

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.FromSeconds(1);

        public void Record(AxiosCircuitContext context)
        {
        }
    }

    /// <summary>永远放行的熔断策略：用来验证「熔断路径上的结果校验」。</summary>
    private sealed class AlwaysAllowed : IAxiosCircuitPolicy
    {
        public bool ShouldAllow(AxiosCircuitContext context) => true;

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.Zero;

        public void Record(AxiosCircuitContext context)
        {
        }
    }

    /// <summary>记录是否被释放的自建凭证。</summary>
    private sealed class Tracker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
