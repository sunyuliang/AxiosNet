using System.Runtime.ExceptionServices;

namespace AxiosNet.Pipeline;

/// <summary>一次尝试里登记的多个跨终端租约的合成：终端把它当成一个 <see cref="IDisposable"/> 处置，或整个交给流式正文的所有权凭证。</summary>
/// <remarks>
/// 不可变链表，最新登记的排在最前，<see cref="Dispose"/> 按登记顺序的逆序释放；只有一个租约时直接裸放，登记第二个才分配节点。
/// </remarks>
internal sealed class AxiosAttemptLeases : IDisposable
{
    /// <summary>本节点持有的租约。</summary>
    private readonly IDisposable _lease;

    /// <summary>更早登记的租约；<c>null</c> = 本节点是最后一个。</summary>
    private readonly AxiosAttemptLeases? _next;

    /// <summary>释放标记：重复 <see cref="Dispose"/> 只生效一次。</summary>
    private int _disposed;

    /// <summary>在链表头挂一个租约。</summary>
    /// <param name="lease">本节点持有的租约。</param>
    /// <param name="next">更早登记的租约；<c>null</c> 表示没有更早的。</param>
    public AxiosAttemptLeases(IDisposable lease, AxiosAttemptLeases? next)
    {
        _lease = lease;
        _next = next;
    }

    /// <summary>一共登记了几个租约。</summary>
    public int Count => _next is null ? 1 : _next.Count + 1;

    /// <summary>逆序释放全部租约；重复调用只生效一次，某个租约抛异常也不跳过其余。</summary>
    /// <remarks>失败会被全部收集：只有一个时按原样重抛（类型与堆栈都保留），多个时抛 <see cref="AggregateException"/>。</remarks>
    public void Dispose()
    {
        // 原子置位：并发的第二次 Dispose 直接返回。
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;

        // 逆序沿链表释放；单个失败只记录，不中断其余项。
        for (AxiosAttemptLeases? node = this; node is not null; node = node._next)
        {
            try
            {
                node._lease.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                (failures ??= new List<Exception>(2)).Add(ex);
            }
        }

        if (failures is null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("多个跨终端租约在释放时失败（按登记顺序的逆序尝试过全部）。", failures);
    }
}
