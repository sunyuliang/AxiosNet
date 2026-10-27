namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 分配测量用例的独占集合：断言「每请求分配多少字节」的用例必须独占进程，
/// 因为计数口径是进程级的 <see cref="GC.GetTotalAllocatedBytes(bool)"/>。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation";
}
