namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 分配测量用例的独占集合：这类用例断言的是「每请求分配多少字节」，必须独占进程才好数。
/// <para>
/// 计数改用进程级 <see cref="GC.GetTotalAllocatedBytes(bool)"/>（它的口径是「整个进程、所有线程」），
/// 所以与其它集合并行跑时，别的用例的分配会算到被测代码头上。实测
/// <c>RequestWithDefaultHeaders_PaysOnlyBclHeaderCost</c> 在并行下虚高到 375 B（护栏 64 B），
/// 单跑与加压跑却全绿 —— 差的是记账口径，不是实现。
/// </para>
/// <para>
/// 为什么不用 <c>GC.GetAllocatedBytesForCurrentThread</c>：它要求被测代码全程跑在同一个线程上，
/// 而请求管线是异步的，续体一旦落到别的线程，这段分配就记不到「当前线程」账上（虚高或虚低都可能）。
/// 用阻塞 <c>GetAwaiter().GetResult()</c> 强行满足这个前提，只是把「测不准」变成「偶发虚高」；
/// 正确做法是让测量本身 <c>async</c>，再换一个不依赖线程归属的计数器。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    public const string Name = "Allocation";
}
