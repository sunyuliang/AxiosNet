using System.ComponentModel;
using System.Reflection;
using AxiosNet.Telemetry;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 观测派发的一次调用成本：接口直调 → 读 <see cref="AxiosTelemetry.Sink"/> → 经护栏派发。
/// <para>
/// <b>为什么单独一个类</b>：池 / 租约的水位与计数走的是「每次事件一次派发」的同步热路径
/// （<c>RecordLease</c> / <c>TagPoolReuse</c> / <c>EnterLease</c>），一次请求里被调到好几次。
/// 可替换观测（<see cref="IAxiosTelemetrySink"/>）换来的是一次虚调用，派发护栏
/// （<c>AxiosTelemetry.Dispatch</c>）又叠了一次虚调用加一个 <c>try/catch</c>——
/// 这两跳值不值，只能在这里按 ns 量出来，端到端基准（回环 socket ≈ 90 µs/请求）根本分辨不出。
/// </para>
/// <para>
/// <b>怎么读</b>：三个变体打的是同一个内置实现（<see cref="BuiltInTelemetrySink"/>），所以
/// 内置那部分工作（Meter 计数器 + 标签）在每一行里都有，差值才是派发层的净成本：
/// <c>Sink_ViaProperty − Sink_Direct</c> = 一次 volatile 读；
/// <c>Sink_ViaGuard − Sink_Direct</c> = 护栏那一跳（虚调用 + 空 catch 块）。
/// 现行库内路径 ≈ 两者之和，与「一次回环往返」相差三个数量级就认为不值得优化。
/// </para>
/// <para>
/// <b>护栏为什么用反射拿</b>：<c>AxiosTelemetry.Dispatch</c> 是 internal（库外不该有第二个派发口），
/// 基准项目不在 <c>InternalsVisibleTo</c> 里。反射只发生在 <see cref="Setup"/>，每操作零成本；
/// 若哪天它改名，这里会立刻抛异常，不会静默测到别的东西。
/// </para>
/// </summary>
[Description("观测派发（池 / 租约热路径）：接口直调 vs 读属性 vs 经护栏")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("没有基线，看绝对 ns 与差值：护栏那一跳应远小于 1 ns 级噪声之外的一次 socket 往返")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class TelemetryDispatchBenchmarks
{
    // 内置实现（默认 Sink）：三个变体背后都是它，差值是纯派发成本。
    private IAxiosTelemetrySink _builtIn = null!;
    // 同一实现的护栏包装（AxiosTelemetry.Dispatch），反射拿一次。
    private IAxiosTelemetrySink _guarded = null!;
    // 两个取值来回切：常量实参会让 JIT 把分支折掉，测到的就不是真实调用。
    private bool _reused;

    /// <summary>取默认实现与派发层各一份，之后每操作不再有反射。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _builtIn = AxiosTelemetry.Sink;

        var dispatch = typeof(AxiosTelemetry).GetProperty("Dispatch", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "找不到 AxiosTelemetry.Dispatch：派发层改名或去掉了，这个基准的第三行会测成别的东西，先改这里。");

        _guarded = (IAxiosTelemetrySink)dispatch.GetValue(null)!;
    }

    /// <summary>接口直调内置实现：只有一次虚调用，是另两行的参照。</summary>
    [Benchmark]
    public void RecordLease_Direct() => _builtIn.RecordLease(_reused = !_reused);

    /// <summary>按本属性当前值派发（本轮之前的库内路径）：多一次 volatile 读。</summary>
    [Benchmark]
    public void RecordLease_ViaProperty() => AxiosTelemetry.Sink.RecordLease(_reused = !_reused);

    /// <summary>经护栏派发（本轮之后的库内路径）：再多一次虚调用与一个空 catch 块。</summary>
    [Benchmark]
    public void RecordLease_ViaGuard() => _guarded.RecordLease(_reused = !_reused);

    /// <summary>标签类事件：与 <c>RecordLease</c> 同一形态，确认结论不是某一个方法的特例。</summary>
    [Benchmark]
    public void TagPoolReuse_Direct() => _builtIn.TagPoolReuse(_reused = !_reused);

    /// <summary>标签类事件经护栏：差值应与 <c>RecordLease</c> 一致。</summary>
    [Benchmark]
    public void TagPoolReuse_ViaGuard() => _guarded.TagPoolReuse(_reused = !_reused);

    /// <summary>租约进出水位（每个池租约一次）：内置实现里是一次 <c>Interlocked</c>。</summary>
    [Benchmark]
    public void EnterLease_Direct() => _builtIn.EnterLease();

    /// <summary>租约进出水位经护栏：确认护栏不改变「一次 <c>Interlocked</c>」的量级。</summary>
    [Benchmark]
    public void EnterLease_ViaGuard() => _guarded.EnterLease();
}
