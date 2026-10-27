namespace AxiosNet.Internal;

/// <summary>
/// 同步入口的阻塞等待助手：把「带令牌的异步核心」同步等待掉。
/// 链路全程 <c>ConfigureAwait(false)</c>，但它终究占一个线程，能用 async 就用 async。
/// </summary>
internal static class BlockingWait
{
    /// <summary>已完成（内存流 / 数据已到达）时直接取结果，不为一次快速返回白建 Task。</summary>
    public static T Wait<T>(ValueTask<T> task)
        => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    public static void Wait(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            task.AsTask().GetAwaiter().GetResult();
        }
    }

    public static void Wait(Task task)
        => task.GetAwaiter().GetResult();
}
