namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 时间敏感用例的独占集合：集合内的用例断言的是「N 秒内必须被打断 / 必须在预算内完成」，
/// 与其它集合并行跑时会被抢走 CPU 与线程池线程，让<b>正确</b>的实现超预算。
/// <para>用 <c>DisableParallelization</c> 而不是放宽预算：预算只是兜底，放宽等于不再验证及时性。</para>
/// <para>
/// 与 <c>PerformanceCollection</c>（同样是独占集合）不冲突：xunit 的非并行集合彼此也是串行执行的。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
