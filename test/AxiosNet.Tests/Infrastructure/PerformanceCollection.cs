namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 性能类用例的独占集合：与 <c>HttpClient</c> 做配对比值时，同进程里任何别的请求
/// 都会污染吞吐数字，所以这组用例必须独占 CPU。
/// </summary>
/// <remarks>
/// 与 <see cref="TimingCollection"/> / <see cref="AllocationCollection"/> 不冲突：
/// xunit 的非并行集合之间同样串行执行。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}
