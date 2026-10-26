namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 时间敏感用例的独占集合：集合内的用例断言的是「N 秒内必须被打断 / 必须在预算内完成」，
/// 与其它集合并行跑时会被抢走 CPU 与线程池线程，让<b>正确</b>的实现超预算。
/// <para>
/// 直接证据：<c>StreamRead_HonorsExternalCancellation</c> 在 GitHub Actions 上耗时 2.34s
/// （本地空闲机器 0.16s），而同期提交里没有任何库代码改动 —— 差的是调度，不是实现。
/// </para>
/// <para>
/// 用 <c>DisableParallelization</c> 而不是放宽预算：预算只是兜底，真正的问题是这个用例
/// 需要「独占的机器」，而不是「更松的尺子」。放宽到 30s 也能绿，但那等于不再验证及时性。
/// </para>
/// <para>
/// 与 <c>PerformanceCollection</c>（同样是独占集合）不冲突：xunit 的非并行集合彼此也是串行执行的。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
