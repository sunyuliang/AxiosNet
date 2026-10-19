namespace AxiosNet.Internal;

/// <summary>
/// 同步入口的阻塞等待助手：把「带令牌的异步核心」同步等待掉。
/// <para>
/// 库内所有流式包装（超时/租约/进度限速）都遵循同一条不变量：
/// <b>同步成员绝不对内层流做同步 I/O</b>——一律阻塞等待对应的异步核心。
/// 这样限速等待天然走 <c>Task.Delay</c>（异步路径），注入的自定义 Handler
/// 即便返回 async-only 的流（同步读会抛 <see cref="NotSupportedException"/>），同步读也照常工作。
/// </para>
/// <para>
/// 阻塞等待不会捕获同步上下文（链路内部全程 <c>ConfigureAwait(false)</c>），
/// 所以在 UI 线程上同步读也不会死锁——除非注入的自定义 Handler 提供的流自己捕获上下文。
/// 它终究占住一个线程，能用 async 就用 async。
/// </para>
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
