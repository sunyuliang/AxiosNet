using AxiosNet.Internal;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 同步读写入口的阻塞桥接（<see cref="BlockingWait"/>）：
/// 值任务已完成时直接取结果，未完成才落到阻塞等待；异常必须原样冒泡（含取消）。
/// </summary>
public sealed class BlockingWaitTests
{
    [Fact]
    public void WaitOfValueTask_CompletedTask_ReturnsResultWithoutBlocking()
    {
        Assert.Equal(42, BlockingWait.Wait(new ValueTask<int>(42)));
    }

    [Fact]
    public void WaitOfValueTask_PendingTask_BlocksUntilResult()
    {
        // 真的未完成：Task.FromResult 之类已经完成的值任务走不到阻塞分支
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(_ => source.SetResult(7));

        Assert.Equal(7, BlockingWait.Wait(new ValueTask<int>(source.Task)));
    }

    [Fact]
    public void WaitOfValueTask_FaultedTask_PropagatesException()
    {
        var task = Task.FromException<int>(new InvalidOperationException("boom"));

        var exception = Assert.Throws<InvalidOperationException>(() => BlockingWait.Wait(new ValueTask<int>(task)));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public void WaitOfNonGenericValueTask_CompletedTask_ReturnsImmediately()
    {
        BlockingWait.Wait(ValueTask.CompletedTask);
    }

    [Fact]
    public void WaitOfNonGenericValueTask_PendingTask_BlocksUntilCompletion()
    {
        var flag = 0;
        var pending = new ValueTask(Task.Run(() => Volatile.Write(ref flag, 1)));

        BlockingWait.Wait(pending);

        Assert.Equal(1, Volatile.Read(ref flag));
    }

    [Fact]
    public void WaitOfNonGenericValueTask_FaultedTask_PropagatesException()
    {
        // FromException 的任务已经完成，走的是「观察既有故障」而不是「阻塞后故障」
        var faulted = new ValueTask(Task.FromException(new InvalidOperationException("boom")));

        Assert.Throws<InvalidOperationException>(() => BlockingWait.Wait(faulted));
    }

    [Fact]
    public void WaitOfTask_PendingTask_BlocksAndPropagatesCancellation()
    {
        // 任务真的未完成，取消发生在等待过程中：Wait 必须先阻塞，再被取消唤醒。
        var cts = new CancellationTokenSource();
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cts.Token.Register(() => source.TrySetCanceled(cts.Token));
        ThreadPool.QueueUserWorkItem(_ => cts.Cancel());

        Assert.ThrowsAny<OperationCanceledException>(() => BlockingWait.Wait(source.Task));
    }
}
