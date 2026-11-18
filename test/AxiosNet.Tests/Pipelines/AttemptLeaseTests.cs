using AxiosNet.Pipeline;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 跨终端租约（<see cref="AxiosAttemptContext.Lease"/> / <see cref="AxiosAttemptContext.WithLease"/>）：
/// 一次尝试可以登记<b>多个</b>资源（闸门槽位 + 中间件自己的会话 / 句柄 / 批处理租约），
/// 登记是追加而不是覆盖，终端统一处置或在流式成功时整个随正文转交。
/// </summary>
/// <remarks>流式与闸门交织的那几条要用队列超时证明「额度确实回来了」，属时序敏感，故整类进 <see cref="TimingCollection"/>。</remarks>
[Collection(TimingCollection.Name)]
public sealed class AttemptLeaseTests
{
    private static AxiosAttemptContext EmptyContext()
        => AxiosAttemptContext.From(
            new AxiosConfig(),
            new Uri("https://host.test/x"),
            dataType: null,
            target: null,
            gate: null,
            deadlineTimestamp: TotalTimeoutBudget.None,
            failoverHealth: null);

    // ------------------------------------------------------------------
    // 上下文里的登记语义
    // ------------------------------------------------------------------

    /// <summary>第一个租约裸放：绝大多数请求只有闸门槽位这一个，不给这条路径加分配。</summary>
    [Fact]
    public void FirstLease_IsStoredAsIs_WithoutWrapping()
    {
        var ctx = EmptyContext();
        Assert.Null(ctx.Lease);

        var lease = new RecordingLease("a", null);
        var derived = ctx.WithLease(lease);

        Assert.Same(lease, derived.Lease);
        Assert.Null(ctx.Lease); // 原上下文没被改动
    }

    /// <summary>核心命题：登记第二个不会顶掉第一个 —— 之前第二个资源无处安放，抢同一格就泄漏前一个。</summary>
    [Fact]
    public void SecondLease_IsAppended_NotOverwritingTheFirst()
    {
        var log = new List<string>();
        var first = new RecordingLease("first", log);
        var second = new RecordingLease("second", log);
        var third = new RecordingLease("third", log);

        var ctx = EmptyContext().WithLease(first).WithLease(second).WithLease(third);

        Assert.NotNull(ctx.Lease);
        Assert.NotSame(first, ctx.Lease); // 三个租约已合成一个
        Assert.Equal(3, Assert.IsType<AxiosAttemptLeases>(ctx.Lease).Count);

        ctx.Lease.Dispose();

        // 逆序释放：后登记的可能依赖先登记的
        Assert.Equal(new[] { "third", "second", "first" }, log);
        Assert.All(new[] { first, second, third }, lease => Assert.Equal(1, lease.DisposeCount));
    }

    /// <summary>合成租约的释放幂等：闸门兜底归还与终端归还叠加是安全的重复释放。</summary>
    [Fact]
    public void Composite_DisposeIsIdempotent()
    {
        var log = new List<string>();
        var first = new RecordingLease("first", log);
        var second = new RecordingLease("second", log);

        var leases = EmptyContext().WithLease(first).WithLease(second).Lease!;
        leases.Dispose();
        leases.Dispose();

        Assert.Equal(new[] { "second", "first" }, log);
    }

    /// <summary>一个租约抛异常也不能跳过其余释放（否则抛错那次就漏资源）。</summary>
    [Fact]
    public void Composite_KeepsReleasing_WhenOneLeaseThrows()
    {
        var log = new List<string>();
        var first = new RecordingLease("first", log);
        var second = new RecordingLease("second", log, throwOnDispose: true);

        var leases = EmptyContext().WithLease(first).WithLease(second).Lease!;

        Assert.Throws<InvalidOperationException>(() => leases.Dispose());
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    /// <summary>
    /// 多个租约都抛异常时，失败要<b>全部</b>报出来：只抛最后一个会让「谁还漏着」这条线索直接消失。
    /// </summary>
    [Fact]
    public void Composite_ReportsEveryFailure_WhenSeveralLeasesThrow()
    {
        var log = new List<string>();
        var first = new RecordingLease("first", log, throwOnDispose: true);
        var second = new RecordingLease("second", log, throwOnDispose: true);

        var leases = EmptyContext().WithLease(first).WithLease(second).Lease!;

        var error = Assert.Throws<AggregateException>(() => leases.Dispose());

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, inner => Assert.IsType<InvalidOperationException>(inner));
        Assert.Equal(new[] { "second", "first" }, log);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    /// <summary><c>WithLease(null)</c> 是「悄悄丢掉前一个租约」的入口，直接拦下。</summary>
    [Fact]
    public void WithLease_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => EmptyContext().WithLease(null!));

    // ------------------------------------------------------------------
    // 与终端 / 闸门的交接
    // ------------------------------------------------------------------

    /// <summary>非流式尝试：中间件登记的租约由终端在返回前处置，不留到下一次尝试。</summary>
    [Fact]
    public async Task ExtraLease_IsDisposedByTerminal_BeforeTheCallReturns()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var extra = new RecordingLease("session", null);
        var pipeline = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
        {
            var attempt = await next(request, ctx.WithLease(extra));
            Assert.True(extra.DisposeCount > 0, "终端应在返回结果前处置上下文里的租约。");
            return attempt;
        });

        using var client = new AxiosClient(pipeline: pipeline);

        Assert.Equal(200, (await client.GetAsync(server.Url + "a")).Status);
        Assert.Equal(1, extra.DisposeCount);
    }

    /// <summary>
    /// 流式成功：登记的全部租约随正文转交给 <c>StreamOwner</c>，正文没读完就释放等于提前放弃占用；
    /// 正文释放后一起归还。
    /// </summary>
    [Fact]
    public async Task ExtraLease_StaysWithTheStream_UntilItIsDisposed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var extra = new RecordingLease("session", null);
        var pipeline = AxiosAttemptPipeline.Default.Append(next => (request, ctx) =>
            next(request, ctx.WithLease(extra)));

        using var client = new AxiosClient(pipeline: pipeline);

        var streamed = await client.GetAsync(server.Url + "a", c => c.ResponseType = ResponseType.Stream);
        var body = (Stream)streamed.Data!;

        Assert.Equal(0, extra.DisposeCount); // 正文还在下载：租约跟着正文走

        body.Dispose();

        Assert.Equal(1, extra.DisposeCount);
    }

    /// <summary>
    /// 闸门槽位与中间件租约共存时互不顶掉：失败后两者都归还，第二个请求立刻拿得到额度
    /// （槽位没回来会卡到队列超时）。
    /// </summary>
    [Fact]
    public async Task GateSlotAndExtraLease_AreBothReleased_AfterAFailure()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        var extra = new RecordingLease("session", null);
        var pipeline = AxiosAttemptPipeline.Default.Append(next => (request, ctx) =>
            next(request, ctx.WithLease(extra)));

        using var client = new AxiosClient(
            new AxiosConfig { Resilience = { Concurrency = { QueueTimeout = TimeSpan.FromMilliseconds(300) } } },
            new AxiosClientOptions { MaxInFlight = 1 },
            pipeline: pipeline);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "a"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(1, extra.DisposeCount);

        // 槽位若没归还，这里会卡到队列超时
        var second = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "b"));
        Assert.Equal(AxiosException.ErrBadResponse, second.Code);
        Assert.Equal(2, extra.DisposeCount);
    }

    /// <summary>记录被释放的次数与顺序的租约。</summary>
    /// <param name="name">记进顺序表的标记。</param>
    /// <param name="log"><c>null</c> = 不记顺序。</param>
    /// <param name="throwOnDispose">释放时是否抛异常（验证「一个抛错不跳过其余」）。</param>
    private sealed class RecordingLease(string name, List<string>? log, bool throwOnDispose = false) : IDisposable
    {
        /// <summary>被释放的次数。</summary>
        public int DisposeCount { get; private set; }

        /// <summary>记一笔释放，必要时抛异常。</summary>
        public void Dispose()
        {
            DisposeCount++;
            log?.Add(name);

            if (throwOnDispose)
            {
                throw new InvalidOperationException($"{name} 释放失败");
            }
        }
    }
}
