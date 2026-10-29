namespace AxiosNet.Internal;

/// <summary>同步入口的阻塞等待助手：把带令牌的异步核心同步等待掉。</summary>
internal static class BlockingWait
{
    /// <summary>已完成时直接取结果，不额外创建 Task。</summary>
    public static T Wait<T>(ValueTask<T> task)
        => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    /// <summary>同步等待无结果的异步核心。</summary>
    public static void Wait(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            task.AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>同步等待一个 <see cref="Task"/>。</summary>
    public static void Wait(Task task)
        => task.GetAwaiter().GetResult();
}
