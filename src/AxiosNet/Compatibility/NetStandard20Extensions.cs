#if NETSTANDARD2_0
// netstandard2.0 兼容资产的 BCL 成员补位：这些是 .NET 5/6 才加进 BCL 的「实例方法 / 重载」，
// 用扩展方法补齐后，业务源码一处都不用改。同名实例方法在 net6.0 / net8.0 上优先，
// 因此这些扩展只在兼容资产里被选中。扩展类放在 AxiosNet 命名空间，所有 AxiosNet.* 子命名空间自动可见。
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxiosNet;

/// <summary>netstandard2.0 缺失的 BCL 实例方法补位（仅在兼容资产编译）。</summary>
internal static class NetStandard20Extensions
{
    // ---- Task.WaitAsync：BCL 版本是 .NET 6 的实例方法，超时抛 TimeoutException、取消抛 TaskCanceledException ----

    /// <summary>给等待加超时；超时不取消原任务，只放弃本次等待。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="timeout">等待上限。</param>
    /// <returns>原任务成功的等待任务。</returns>
    public static Task WaitAsync(this Task task, TimeSpan timeout)
        => WaitAsyncCore(task, timeout, CancellationToken.None);

    /// <summary>给等待加取消令牌；取消不取消原任务，只放弃本次等待。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原任务成功的等待任务。</returns>
    public static Task WaitAsync(this Task task, CancellationToken cancellationToken)
        => WaitAsyncCore(task, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>给等待同时加超时与取消令牌。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="timeout">等待上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原任务成功的等待任务。</returns>
    public static Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
        => WaitAsyncCore(task, timeout, cancellationToken);

    /// <summary>给带结果的等待加超时。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="timeout">等待上限。</param>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <returns>原任务的结果。</returns>
    public static Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout)
        => WaitAsyncCore(task, timeout, CancellationToken.None);

    /// <summary>给带结果的等待加取消令牌。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <returns>原任务的结果。</returns>
    public static Task<TResult> WaitAsync<TResult>(this Task<TResult> task, CancellationToken cancellationToken)
        => WaitAsyncCore(task, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>给带结果的等待同时加超时与取消令牌。</summary>
    /// <param name="task">被等待的任务。</param>
    /// <param name="timeout">等待上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <typeparam name="TResult">结果类型。</typeparam>
    /// <returns>原任务的结果。</returns>
    public static Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout, CancellationToken cancellationToken)
        => WaitAsyncCore(task, timeout, cancellationToken);

    private static async Task WaitAsyncCore(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || (!cancellationToken.CanBeCanceled && timeout == Timeout.InfiniteTimeSpan))
        {
            await task.ConfigureAwait(false);
            return;
        }

        // 与 BCL 同构的三方裁决：原任务完成 / 超时 / 取消先到者决定结果。
        // 超时用 CancellationTokenSource.CancelAfter（一个可释放的定时器），不再额外创建 Task.Delay：
        // 原任务先完成时不会留下一个继续跑到超时点的定时器任务。
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var timeoutRegistration = timeoutSource?.Token.Register(
            () => signal.TrySetException(new TimeoutException($"任务在 {timeout} 内没有完成。")));
        using var cancellationRegistration = cancellationToken.Register(
            () => signal.TrySetCanceled(cancellationToken));

        // 原任务完成时把结果 / 原始异常转到 signal 上；不保存这个续延任务（它可能还没跑完，不能 Dispose）。
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var signal = (TaskCompletionSource<bool>)state!;
                if (completed.IsCanceled)
                {
                    signal.TrySetCanceled();
                }
                else if (completed.IsFaulted)
                {
                    signal.TrySetException(completed.Exception!.InnerExceptions);
                }
                else
                {
                    signal.TrySetResult(true);
                }
            },
            signal,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await signal.Task.ConfigureAwait(false);

        // 走到这里说明原任务已完成：再 await 一次，把结果或原始异常原样抛出。
        await task.ConfigureAwait(false);
    }

    private static async Task<TResult> WaitAsyncCore<TResult>(Task<TResult> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await WaitAsyncCore((Task)task, timeout, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    // ---- 集合：Dictionary.TryAdd / ConcurrentDictionary.TryRemove(KeyValuePair) ----

    /// <summary>键不存在时写入；等价于 .NET Core 2.0+ 的 Dictionary.TryAdd。</summary>
    /// <param name="dictionary">目标字典。</param>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    /// <returns>是否真的写入。</returns>
    public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key))
        {
            return false;
        }

        dictionary.Add(key, value);
        return true;
    }

    /// <summary>按键值对原子移除；等价于 .NET 5+ 的 ConcurrentDictionary.TryRemove(KeyValuePair)。</summary>
    /// <param name="dictionary">目标并发字典。</param>
    /// <param name="item">要移除的键值对（键与值都匹配才移除）。</param>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    /// <returns>是否真的移除。</returns>
    public static bool TryRemove<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary, KeyValuePair<TKey, TValue> item)
        => ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(item);

    // ---- Stream：Memory 版读写与异步释放 ----

    /// <summary>Memory 版异步读；兼容资产上零分配转发到数组重载。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="buffer">读缓冲区。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际读到的字节数。</returns>
    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // 调用方几乎都传 ArrayPool 数组切出的 Memory：TryGetArray 直接取出同一块底层数组，
        // 一次读取零分配。只有非数组支撑的 Memory 才回落到租借缓冲（旧实现每次都 new 出整块）。
        if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment) && segment.Array is not null)
        {
            return await stream.ReadAsync(segment.Array, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = await stream.ReadAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                rented.AsSpan(0, read).CopyTo(buffer.Span);
            }

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>ReadWriteSpan 版同步写：netstandard2.0 的 Stream.Write 只接受数组。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="buffer">待写字节。</param>
    public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            stream.Write(rented, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Encoding.GetBytes(ReadOnlySpan, Span)：netstandard2.0 只有数组版重载。</summary>
    /// <param name="encoding">编码。</param>
    /// <param name="chars">源字符。</param>
    /// <param name="bytes">目标字节区间。</param>
    /// <returns>写入的字节数。</returns>
    public static int GetBytes(this Encoding encoding, ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        if (chars.IsEmpty)
        {
            return 0;
        }

        var rentedChars = ArrayPool<char>.Shared.Rent(chars.Length);
        var rentedBytes = ArrayPool<byte>.Shared.Rent(encoding.GetMaxByteCount(chars.Length));
        try
        {
            chars.CopyTo(rentedChars);
            var written = encoding.GetBytes(rentedChars, 0, chars.Length, rentedBytes, 0);
            rentedBytes.AsSpan(0, written).CopyTo(bytes);
            return written;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars);
            ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    /// <summary>Encoding.GetString(ReadOnlySpan&lt;byte&gt;)：netstandard2.0 只有数组版重载。</summary>
    /// <param name="encoding">编码。</param>
    /// <param name="bytes">源字节。</param>
    /// <returns>解码出的字符串。</returns>
    public static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
        try
        {
            bytes.CopyTo(rented);
            return encoding.GetString(rented, 0, bytes.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 带令牌地读正文为字节数组；netstandard2.0 的 HttpContent 没有该重载。
    /// 令牌在进入 / 返回两个边界真正生效（内部缓冲由 HttpContent 自己完成，无法逐块中断）。
    /// </summary>
    /// <param name="content">正文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>正文字节。</returns>
    public static async Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    /// <summary>ReadOnlyMemory 版异步写；兼容资产上零分配转发到数组重载。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="buffer">待写字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写完成的任务。</returns>
    public static ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return default;
        }

        if (MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array is not null)
        {
            return new ValueTask(stream.WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken));
        }

        return WriteRentedAsync(stream, buffer, cancellationToken);
    }

    private static async ValueTask WriteRentedAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.Span.CopyTo(rented);
            await stream.WriteAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>异步释放：netstandard2.0 的 Stream 没有该契约，退化成同步释放以支撑 await using 模式。</summary>
    /// <param name="stream">要释放的流。</param>
    /// <returns>已完成的任务。</returns>
    public static ValueTask DisposeAsync(this Stream stream)
    {
        stream.Dispose();
        return default;
    }

    // ---- HttpContent：带令牌的重载 ----

    /// <summary>带令牌的复制写出；netstandard2.0 没有该重载，令牌只能在进入 / 返回两个边界生效（保留流式写出）。</summary>
    /// <param name="content">正文。</param>
    /// <param name="stream">目标流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写出任务。</returns>
    public static async Task CopyToAsync(this HttpContent content, Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await content.CopyToAsync(stream, null).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>带令牌地取响应流；netstandard2.0 没有该重载，令牌在进入边界生效。</summary>
    /// <param name="content">正文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>正文流。</returns>
    public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStreamAsync();
    }

    // ---- 字符串 / Span ----

    /// <summary>string.Contains(char)：netstandard2.0 只有 string 重载。</summary>
    /// <param name="text">文本。</param>
    /// <param name="value">要找的字符。</param>
    /// <returns>是否包含。</returns>
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;

    /// <summary>string.StartsWith(char)：netstandard2.0 只有 string 重载。</summary>
    /// <param name="text">文本。</param>
    /// <param name="value">要比较的字符。</param>
    /// <returns>是否以该字符开头。</returns>
    public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;

    /// <summary>ReadOnlySpan.StartsWith(char)：netstandard2.0 的 System.Memory 不提供该扩展。</summary>
    /// <param name="span">字符区间。</param>
    /// <param name="value">要比较的字符。</param>
    /// <returns>是否以该字符开头。</returns>
    public static bool StartsWith(this ReadOnlySpan<char> span, char value) => span.Length > 0 && span[0] == value;

    /// <summary>Encoding.GetCharCount(ReadOnlySpan&lt;byte&gt;)：netstandard2.0 只有数组版重载。</summary>
    /// <param name="encoding">编码。</param>
    /// <param name="bytes">源字节。</param>
    /// <returns>解码后的字符数。</returns>
    public static int GetCharCount(this Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
        try
        {
            bytes.CopyTo(rented);
            return encoding.GetCharCount(rented, 0, bytes.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>ReadOnlySpan.Contains(char)：netstandard2.0 的 System.Memory 不提供该扩展。</summary>
    /// <param name="span">字符区间。</param>
    /// <param name="value">要找的字符。</param>
    /// <returns>是否包含。</returns>
    public static bool Contains(this ReadOnlySpan<char> span, char value) => span.IndexOf(value) >= 0;

    // ---- 反射 ----

    /// <summary>MethodInfo.CreateDelegate&lt;T&gt;()：netstandard2.0 只有非泛型重载。</summary>
    /// <param name="method">方法信息。</param>
    /// <typeparam name="TDelegate">委托类型。</typeparam>
    /// <returns>创建的委托。</returns>
    public static TDelegate CreateDelegate<TDelegate>(this MethodInfo method) where TDelegate : Delegate
        => (TDelegate)method.CreateDelegate(typeof(TDelegate));
}
#endif
