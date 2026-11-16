using AxiosNet;
using AxiosNet.Telemetry;

namespace AxiosNet.Simple.Examples;

/// <summary>40. 可替换观测：派生 AxiosTelemetrySink 接管埋点，Off 一键静音，写错也不影响请求。</summary>
public sealed class TelemetrySinkExample : IExample
{
    public int Number => 40;

    public string Title => "可替换观测：AxiosTelemetry.Sink 接管埋点 / Off 静音 / 写错不影响请求";

    /// <summary>示例入口：可替换观测：AxiosTelemetry.Sink 接管埋点 / Off 静音 / 写错不影响请求。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 进程级静态：进来先存一份，出去必须还原 —— 否则后面的示例、以及内置 span / 指标会被永久掐掉
        var original = AxiosTelemetry.Sink;
        try
        {
            // 1) 请求级埋点：IsEnabled 返回 true 之后，才会收到开句柄与成功 / 失败上报
            var lifecycle = new LifecycleSink();
            AxiosTelemetry.Sink = lifecycle;

            await Axios.GetAsync(context.BaseUrl + "users/1");
            try
            {
                await Axios.GetAsync(context.BaseUrl + "not-found");
            }
            catch (AxiosException)
            {
                // 404 在 validateStatus 这里就被拒了：失败照样会上报给 sink
            }

            Console.WriteLine($"  请求级埋点: 成功 {lifecycle.Succeeded} 次 / 失败 {lifecycle.Failed} 次");
            Console.WriteLine("  换掉 Sink = 整套接管：内置的 ActivitySource / Meter 同时停用（不是叠加）");

            // 2) 事件类不受 IsEnabled 管辖：只覆写 RecordLease 就收得到，哪怕开关仍是 false
            var poolEvents = new PoolEventSink();
            AxiosTelemetry.Sink = poolEvents;

            await Axios.GetAsync(context.BaseUrl + "users/1");

            Console.WriteLine($"  池事件: IsEnabled = {poolEvents.IsEnabled(null)}, 但租约记了 {poolEvents.Leases} 次（复用 {poolEvents.Reused} 次）");
            Console.WriteLine("  缓存 / 重试 / 闸门 / 池 / 租约都属这一类：覆写就会来，要整体静音用 Off");

            // 3) 整体静音：Off 是空实现，请求语义一点不变
            AxiosTelemetry.Sink = AxiosTelemetrySink.Off;

            var silenced = await Axios.GetAsync(context.BaseUrl + "users/1");
            Console.WriteLine($"  Off 之后: 状态 {silenced.Status}（埋点全关，请求照常）");

            // 4) 违约实现：观测里抛异常只丢那一次观测，异常在派发层结束，不会冒到调用方
            AxiosTelemetry.Sink = new ThrowingSink();

            var stillOk = await Axios.GetAsync(context.BaseUrl + "users/1");
            Console.WriteLine($"  观测里抛异常时: 状态 {stillOk.Status}（派发层吞掉违约异常，结果不被改写）");
        }
        finally
        {
            AxiosTelemetry.Sink = original;
        }

        Console.WriteLine("  提示：只在启动时设一次；有在途请求 / 未归还租约时替换，成对水位会两边各错一半");
    }

    /// <summary>只做请求级埋点：覆写 IsEnabled 打开开关，再收成功 / 失败。</summary>
    private sealed class LifecycleSink : AxiosTelemetrySink
    {
        /// <summary>成功计数（多线程并发上报，用 Interlocked）。</summary>
        private int _succeeded;

        /// <summary>失败计数（多线程并发上报，用 Interlocked）。</summary>
        private int _failed;

        /// <summary>成功次数。</summary>
        public int Succeeded => Volatile.Read(ref _succeeded);

        /// <summary>失败次数。</summary>
        public int Failed => Volatile.Read(ref _failed);

        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override void RequestSucceeded(
            AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
            => Interlocked.Increment(ref _succeeded);

        /// <inheritdoc />
        public override void RequestFailed(
            AxiosConfig? config, Uri? uri, Exception exception, long startTimestamp, in AxiosRequestTrace trace)
            => Interlocked.Increment(ref _failed);
    }

    /// <summary>只收池事件：刻意不覆写 IsEnabled，用来证明事件类不受这个开关管辖。</summary>
    private sealed class PoolEventSink : AxiosTelemetrySink
    {
        /// <summary>租约次数。</summary>
        private int _leases;

        /// <summary>其中复用已有节点的次数。</summary>
        private int _reused;

        /// <summary>租约总次数。</summary>
        public int Leases => Volatile.Read(ref _leases);

        /// <summary>复用已有节点的次数。</summary>
        public int Reused => Volatile.Read(ref _reused);

        /// <inheritdoc />
        public override void RecordLease(bool reused)
        {
            Interlocked.Increment(ref _leases);
            if (reused)
            {
                Interlocked.Increment(ref _reused);
            }
        }
    }

    /// <summary>违约实现：所有成员都不该抛，这里故意抛，验证「观测坏掉不等于请求坏掉」。</summary>
    private sealed class ThrowingSink : AxiosTelemetrySink
    {
        /// <inheritdoc />
        public override bool IsEnabled(AxiosConfig? config) => true;

        /// <inheritdoc />
        public override void RequestSucceeded(
            AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
            => throw new InvalidOperationException("故意抛：模拟第三方观测实现违约");
    }
}
