using System.Diagnostics;
using AxiosNet.Telemetry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Diagnostics;

/// <summary>
/// 观测实现可整套替换：<see cref="AxiosTelemetry.Sink"/> 换上自己的实现后，内置的
/// ActivitySource / Meter / DiagnosticListener <b>全部停用</b>（不是叠加）；设成
/// <see cref="AxiosTelemetrySink.Off"/> 则彻底关掉。
/// </summary>
/// <remarks>
/// 观测点是进程级的（换 Sink 会影响同进程所有请求），因此进 <see cref="TelemetryCollection"/> 独占运行。
/// </remarks>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetrySinkTests
{
    /// <summary>默认实现仍是内置那套：观测开着（<see cref="AxiosConfig.ObservabilitySettings.Capture"/> 的默认值）。</summary>
    [Fact]
    public void DefaultSink_IsTheBuiltInOne()
    {
        Assert.NotSame(AxiosTelemetrySink.Off, AxiosTelemetry.Sink);
        Assert.True(AxiosTelemetry.Sink.IsEnabled(null));
        Assert.False(AxiosTelemetrySink.Off.IsEnabled(null));
    }

    /// <summary>换实现后请求照常成功，内置 span 一条都不产，观测事件全部落到自定义实现上。</summary>
    [Fact]
    public async Task CustomSink_TakesOver_AndBuiltInStopsProducingSpans()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var spans = new SpanRecorder();

        var sink = new RecordingSink();
        var original = AxiosTelemetry.Sink;
        int status;
        try
        {
            AxiosTelemetry.Sink = sink;

            using var client = new AxiosClient();
            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);

        // 内置那套完全没被碰过
        Assert.Equal(0, spans.Count);

        // 自定义实现拿到了完整的一轮：开始 → 成功 → 结束，句柄是活的
        Assert.Equal(1, sink.StartedCount);
        Assert.True(sink.Succeeded);
        Assert.Equal(1, sink.StoppedCount);
        Assert.False(sink.TraceWasDead);
        Assert.Equal(server.Url + "x", sink.Uri);
    }

    /// <summary>
    /// 请求<b>在途时</b>换掉实现：收尾必须回到「开观测的那个实现」上，否则自定义实现会收到 Stop 却从没收到 Start
    /// （在途计数与 span 生命周期一起错位）。事件类回调按当前值派发，是刻意的另一半语义。
    /// </summary>
    /// <remarks>
    /// 这条钉的是「把 <c>StopRequest</c> 从直调 <see cref="AxiosTelemetry.Sink"/> 改成走观测句柄」那处修复。
    /// 换回直调当前实现，这里就会变成 started 收不到 Stop、swapped 收到一个它没开过的 Stop。
    /// </remarks>
    [Fact]
    public async Task SinkSwappedMidFlight_StopGoesBackToTheSinkThatStartedIt()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new RecordingSink();
        var swapped = new RecordingSink();

        var original = AxiosTelemetry.Sink;
        int status;
        try
        {
            AxiosTelemetry.Sink = started;

            using var client = new AxiosClient(handler: new BlockingHandler(release.Task));
            var pending = client.GetAsync("http://stub.test/x");

            // 先等它真的进入发送阶段（StartRequest 已经发生过），再换实现
            await WaitUntilAsync(() => started.StartedCount > 0, "请求进入发送阶段");

            AxiosTelemetry.Sink = swapped;
            release.SetResult(true);
            status = (await pending).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);

        // 收尾回到开它的实现上：Start / Stop / 成败上报 必须落在同一家
        Assert.Equal(1, started.StartedCount);
        Assert.Equal(1, started.StoppedCount);
        Assert.Equal(0, swapped.StartedCount);
        Assert.Equal(0, swapped.StoppedCount);

        // 成败上报带着句柄（句柄里的 State 是开它的那家自己的载体），所以也归开它的实现：
        // 把它发给另一家等于让对方去解析别人格式的 State。
        Assert.True(started.Succeeded);
        Assert.False(swapped.Succeeded);
    }

    /// <summary><see cref="AxiosTelemetrySink.Off"/> 把观测彻底关掉，而请求语义不变。</summary>
    [Fact]
    public async Task OffSink_ProducesNothing_ButRequestStillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var spans = new SpanRecorder();

        var original = AxiosTelemetry.Sink;
        int status;
        try
        {
            AxiosTelemetry.Sink = AxiosTelemetrySink.Off;

            using var client = new AxiosClient();
            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);
        Assert.Equal(0, spans.Count);
    }

    /// <summary>赋 <c>null</c> 是「不小心把观测弄没了」的入口，直接拦下（要关掉请用 <see cref="AxiosTelemetrySink.Off"/>）。</summary>
    [Fact]
    public void Sink_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => AxiosTelemetry.Sink = null!);

    /// <summary>
    /// 观测实现违约抛异常时，<b>请求结果与库的不变量都不能被改写</b>：请求照常成功，
    /// 在途计数照常归零（否则 <c>Dispose()</c> 永远等不到 0，池与 Handler 一直不释放），span 照常收。
    /// </summary>
    /// <remarks>
    /// 契约上实现「不应该抛出」，但可替换实现是第三方代码，而且收尾那一步在 <c>finally</c> 里：
    /// 异常一旦外溢，一条 200 就被换成 <see cref="InvalidOperationException"/>，真正的失败原因也会被盖掉。
    /// 所以派发层（<c>AxiosTelemetry.Dispatch</c>）把异常就地吞掉，三段收尾彼此不连坐。
    /// </remarks>
    [Fact]
    public async Task SinkThrowingOnExitRequest_StillSucceedsAndReleasesTheClient()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var original = AxiosTelemetry.Sink;
        var sink = new ThrowingSink();
        using var client = new AxiosClient();
        int status;
        try
        {
            AxiosTelemetry.Sink = sink;

            // 观测写错了只该丢掉观测：请求本身仍然是一条成功的 200。
            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);

        // 「出」被调到一次（异常在派发层结束），span 照常收。
        Assert.Equal(1, sink.Exits);
        Assert.Equal(1, sink.Stops);

        client.Dispose();

        // 在途计数归零 → Dispose 当场释放资源；漏归还会让这两个数字停在 1 / 0。
        Assert.Equal(0, PrivateInt(client, "_inFlight"));
        Assert.Equal(1, PrivateInt(client, "_resourcesDisposed"));
    }

    /// <summary>
    /// 「进」那一步抛异常时，<b>不能</b>再补一次「出」：没配对的水位会把实现在途计数打成负数，
    /// 此后所有读数都是假的。配对与否由派发层的护栏在同一份快照上判定（进成功才置位）。
    /// </summary>
    /// <remarks>
    /// 与上一条同一族：入口标记若在进入成功之前就置位，收尾分支会当成「已经进过」。
    /// 请求本身同样不受影响 —— 观测挂了不等于请求挂了。
    /// </remarks>
    [Fact]
    public async Task SinkThrowingOnEnterRequest_GetsNoPairedExitAndClientStillSucceeds()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var original = AxiosTelemetry.Sink;
        var sink = new ThrowingEnterSink();
        using var client = new AxiosClient();
        int status;
        try
        {
            AxiosTelemetry.Sink = sink;

            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);

        Assert.Equal(1, sink.Enters);
        Assert.Equal(0, sink.Exits);

        // 没进去就没有句柄：既不该开 span，也不该收。
        Assert.Equal(0, sink.Starts);
        Assert.Equal(0, sink.Stops);

        client.Dispose();
        Assert.Equal(0, PrivateInt(client, "_inFlight"));
        Assert.Equal(1, PrivateInt(client, "_resourcesDisposed"));
    }

    /// <summary>事件类回调抛异常同样只丢事件：缓存 / 池 / 闸门 的观测写错不能把请求变成失败。</summary>
    [Fact]
    public async Task SinkThrowingOnEvents_DoesNotAffectTheRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var original = AxiosTelemetry.Sink;
        var sink = new ThrowingEventSink();
        using var client = new AxiosClient();
        int status;
        try
        {
            AxiosTelemetry.Sink = sink;

            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);
        Assert.True(sink.EventsSeen > 0);
    }

    /// <summary>句柄绕过派发层直连实现：开观测抛异常时退化成「本次没有观测」，请求照常发。</summary>
    [Fact]
    public async Task SinkThrowingOnStartRequest_DoesNotAffectTheRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var original = AxiosTelemetry.Sink;
        var sink = new ThrowingStartSink();
        using var client = new AxiosClient();
        int status;
        try
        {
            AxiosTelemetry.Sink = sink;

            status = (await client.GetAsync(server.Url + "x")).Status;
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Assert.Equal(200, status);
        Assert.Equal(1, sink.Starts);
        Assert.Equal(0, sink.Stops);
    }

    /// <summary>读实例私有 <see cref="int"/> 字段（护栏用例钉的是「库自己怎么记数」，只能这么看）。</summary>
    private static int PrivateInt(AxiosClient target, string name)
        => (int)typeof(AxiosClient)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(target)!;

    /// <summary>订阅本库的 span 源并计数：用来证明「换掉实现之后内置那套确实一条都没建」。</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;

        /// <summary>已创建的 span 数。</summary>
        private int _count;

        /// <summary>挂上一个只听本库 span 源的监听器。</summary>
        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AxiosTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = _ => Interlocked.Increment(ref _count)
            };

            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>已创建的 span 数。</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>摘掉监听器。</summary>
        public void Dispose() => _listener.Dispose();
    }

    /// <summary>记录调用链的自定义实现。</summary>
    private sealed class RecordingSink : AxiosTelemetrySink
    {
        /// <summary><see cref="StartRequest"/> 被调用了几次。</summary>
        private int _started;

        /// <summary><see cref="StopRequest"/> 被调用了几次。</summary>
        private int _stopped;

        /// <summary><see cref="StartRequest"/> 被调用了几次。</summary>
        public int StartedCount => Volatile.Read(ref _started);

        /// <summary><see cref="StopRequest"/> 被调用了几次。</summary>
        public int StoppedCount => Volatile.Read(ref _stopped);

        /// <summary>是否收到成功事件。</summary>
        public bool Succeeded { get; private set; }

        /// <summary>交出去的句柄是否不可用（应当为 <c>false</c>）。</summary>
        public bool TraceWasDead { get; private set; }

        /// <summary>成功事件里带的 URL。</summary>
        public string? Uri { get; private set; }

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
        {
            Interlocked.Increment(ref _started);
            return new AxiosRequestTrace(this, uri);
        }

        /// <inheritdoc />
        public override void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
        {
            Succeeded = true;
            TraceWasDead = !trace.IsActive;
            Uri = uri.AbsoluteUri;
        }

        /// <inheritdoc />
        public override void StopRequest(in AxiosRequestTrace trace)
        {
            if (trace.IsActive)
            {
                Interlocked.Increment(ref _stopped);
            }
        }
    }

    /// <summary>把请求卡在发送阶段的 Handler：让测试有机会「等它进去之后再换实现」。</summary>
    private sealed class BlockingHandler(Task release) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>违约实现：在「请求离开」那一步抛异常，并记录 span 有没有被收（应当照收）。</summary>
    private sealed class ThrowingSink : AxiosTelemetrySink
    {
        private int _exits;
        private int _stops;

        /// <summary><see cref="ExitRequest"/> 被调用几次（异常应停在派发层，调用点看到的是正常返回）。</summary>
        public int Exits => Volatile.Read(ref _exits);

        /// <summary><see cref="StopRequest"/> 被调用几次。</summary>
        public int Stops => Volatile.Read(ref _stops);

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri) => new(this, state: null);

        /// <inheritdoc />
        public override void StopRequest(in AxiosRequestTrace trace) => Interlocked.Increment(ref _stops);

        /// <inheritdoc />
        public override void ExitRequest()
        {
            Interlocked.Increment(ref _exits);
            throw new InvalidOperationException("观测实现违约");
        }
    }

    /// <summary>违约实现：在「请求进入」那一步抛异常，并记录「出」被叫了几次（不该有）。</summary>
    private sealed class ThrowingEnterSink : AxiosTelemetrySink
    {
        private int _enters;
        private int _exits;
        private int _starts;
        private int _stops;

        /// <summary>「进」被调用几次。</summary>
        public int Enters => Volatile.Read(ref _enters);

        /// <summary>「出」被调用几次。</summary>
        public int Exits => Volatile.Read(ref _exits);

        /// <summary>开观测被调用几次（没进去就不该开）。</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <summary>收观测被调用几次（没开过就不该收）。</summary>
        public int Stops => Volatile.Read(ref _stops);

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
        {
            Interlocked.Increment(ref _starts);
            return new AxiosRequestTrace(this, state: null);
        }

        /// <inheritdoc />
        public override void StopRequest(in AxiosRequestTrace trace) => Interlocked.Increment(ref _stops);

        /// <inheritdoc />
        public override void EnterRequest()
        {
            Interlocked.Increment(ref _enters);
            throw new InvalidOperationException("观测实现违约");
        }

        /// <inheritdoc />
        public override void ExitRequest() => Interlocked.Increment(ref _exits);
    }

    /// <summary>违约实现：事件类回调全部抛异常（缓存 / 闸门 / 池 / 重试都在其中）。</summary>
    private sealed class ThrowingEventSink : AxiosTelemetrySink
    {
        private int _events;

        /// <summary>被调到的事件次数（证明真的走到了那些回调，而不是「一个都没触发所以没出事」）。</summary>
        public int EventsSeen => Volatile.Read(ref _events);

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri) => new(this, state: null);

        /// <inheritdoc />
        public override void CacheHit(AxiosConfig? config) => Blow();

        /// <inheritdoc />
        public override void CacheMiss(AxiosConfig? config) => Blow();

        /// <inheritdoc />
        public override void RecordLease(bool reused) => Blow();

        /// <inheritdoc />
        public override void TagPoolReuse(bool reused) => Blow();

        /// <inheritdoc />
        public override void EnterLease() => Blow();

        /// <inheritdoc />
        public override void ExitLease() => Blow();

        /// <inheritdoc />
        public override void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
            => Blow();

        /// <summary>记一次「确实被调到」然后违约。</summary>
        private void Blow()
        {
            Interlocked.Increment(ref _events);
            throw new InvalidOperationException("观测实现违约");
        }
    }

    /// <summary>违约实现：开观测那一步抛异常（句柄路径绕过派发层，靠调用点的 try/catch 兜）。</summary>
    private sealed class ThrowingStartSink : AxiosTelemetrySink
    {
        private int _starts;
        private int _stops;

        /// <summary>开观测被调用几次。</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <summary>收观测被调用几次（没开成就没有句柄，不该收）。</summary>
        public int Stops => Volatile.Read(ref _stops);

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
        {
            Interlocked.Increment(ref _starts);
            throw new InvalidOperationException("观测实现违约");
        }

        /// <inheritdoc />
        public override void StopRequest(in AxiosRequestTrace trace) => Interlocked.Increment(ref _stops);
    }

    /// <summary>轮询等待某个条件成立；超时即失败（带上在等什么）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail($"等待超时：{what}");
    }
}
