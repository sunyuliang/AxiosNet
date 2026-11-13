#if NET8_0_OR_GREATER
using System.Diagnostics;
using Xunit.Abstractions;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 兼容 shim 的 A/B 对照：对照基线按上一版 shim 的写法复刻（不是从 git 历史检出——这些文件在本次变更里
/// 都是新增的）。分配类用例断言分配字节数；耗时类用例只输出数字、不设门槛。这类微基准对机器负载敏感，
/// 标记为 <c>Category=Evidence</c>：CI 默认过滤掉，只在本地显式运行时作为诊断证据。
/// </summary>
[Trait("Category", "Evidence")]
public sealed class ShimEvidenceTests
{
    private const int BufferSize = 64 * 1024;
    private const int Iterations = 200;

    private readonly ITestOutputHelper _output;

    public ShimEvidenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void 读_shim_不再按块分配_旧实现每次复制整块()
    {
        var buffer = new byte[BufferSize];
        var stream = new NullStream();

        // NullStream 同步完成，这里用 GetResult 直接消费 ValueTask：
        // 避免把 ValueTask.AsTask() 自身的 Task 分配算进 shim 头上。
        var newBytes = Measure(
            () => _ = NetStandard20Extensions.ReadAsync(stream, buffer.AsMemory(), default).GetAwaiter().GetResult(),
            Iterations);
        var oldBytes = Measure(
            () => _ = OldReadAsync(stream, buffer.AsMemory(), default).GetAwaiter().GetResult(),
            Iterations);

        // 旧实现：每次读都 new byte[buffer.Length]。
        Assert.True(oldBytes > 10L * 1024 * 1024, $"旧实现应分配 > 10MiB，实际 {oldBytes} 字节");
        // 新实现：底层是数组时直接转发。Release 下为 0；Debug 下 async 状态机是类，
        // 每次调用会分配一个约 120 字节的装箱状态机，但已与块大小无关。
        Assert.True(newBytes < 64 * 1024, $"新实现分配了 {newBytes} 字节，期望 < 64KiB");
        Assert.True(newBytes * 100 < oldBytes, $"新实现 {newBytes} 字节，旧实现 {oldBytes} 字节");
        _output.WriteLine(
            $"ReadAsync x{Iterations}（{BufferSize / 1024} KiB/次）：旧 {oldBytes:N0} 字节 → 新 {newBytes:N0} 字节，降幅 {(double)oldBytes / Math.Max(newBytes, 1):N0}x");
    }

    [Fact]
    public async Task 异步写_shim_不再按块分配_旧实现每次复制整块()
    {
        var buffer = new byte[BufferSize];
        var stream = new NullStream();

        var newBytes = await MeasureAsync(
            () => NetStandard20Extensions.WriteAsync(stream, buffer.AsMemory(), default).AsTask(),
            Iterations);
        var oldBytes = await MeasureAsync(
            () => OldWriteAsync(stream, buffer.AsMemory(), default),
            Iterations);

        Assert.True(oldBytes > 10L * 1024 * 1024, $"旧实现应分配 > 10MiB，实际 {oldBytes} 字节");
        Assert.True(newBytes < 4 * 1024, $"新实现分配了 {newBytes} 字节，期望 < 4KiB");
        Assert.True(newBytes * 1000 < oldBytes, $"新实现 {newBytes} 字节，旧实现 {oldBytes} 字节");
        _output.WriteLine(
            $"WriteAsync x{Iterations}（{BufferSize / 1024} KiB/次）：旧 {oldBytes:N0} 字节 → 新 {newBytes:N0} 字节，降幅 {(double)oldBytes / Math.Max(newBytes, 1):N0}x");
    }

    [Fact]
    public void 同步写_shim_用租借缓冲_旧实现每次_ToArray()
    {
        var buffer = new byte[BufferSize];
        var stream = new NullStream();

        var newBytes = Measure(
            () => NetStandard20Extensions.Write(stream, buffer.AsSpan()),
            Iterations);
        var oldBytes = Measure(
            () =>
            {
                var copy = buffer.ToArray();
                stream.Write(copy, 0, copy.Length);
            },
            Iterations);

        Assert.True(oldBytes > 10L * 1024 * 1024, $"旧实现应分配 > 10MiB，实际 {oldBytes} 字节");
        Assert.True(newBytes < 4 * 1024, $"新实现分配了 {newBytes} 字节，期望 < 4KiB（ArrayPool 摊销）");
        Assert.True(newBytes * 1000 < oldBytes, $"新实现 {newBytes} 字节，旧实现 {oldBytes} 字节");
        _output.WriteLine(
            $"Stream.Write x{Iterations}（{BufferSize / 1024} KiB/次）：旧 {oldBytes:N0} 字节 → 新 {newBytes:N0} 字节，降幅 {(double)oldBytes / Math.Max(newBytes, 1):N0}x");
    }

    [Fact]
    public void IsByRefLike_缓存委托与反射取值的耗时对比_仅诊断()
    {
        var type = typeof(string);
        const int iterations = 200_000;
        var property = typeof(Type).GetProperty("IsByRefLike")!;

        // 预热，避免把 JIT / 首次反射开销算进对比。
        for (var i = 0; i < 1000; i++)
        {
            _ = typeof(Type).GetProperty("IsByRefLike")?.GetValue(type);
            _ = property.GetValue(type);
            _ = PlatformCompat.IsByRefLike(type);
        }

        var lookupTicks = 0L;  // 基线 A：每次重新取 PropertyInfo 再 GetValue
        var cachedTicks = 0L;  // 基线 B：缓存 PropertyInfo，仍每次 GetValue
        var newTicks = 0L;     // 现实现：缓存强类型 getter 委托
        for (var round = 0; round < 5; round++)
        {
            lookupTicks += MeasureTicks(
                () => _ = typeof(Type).GetProperty("IsByRefLike")?.GetValue(type) is bool v && v,
                iterations);
            cachedTicks += MeasureTicks(
                () => _ = property.GetValue(type) is bool v && v,
                iterations);
            newTicks += MeasureTicks(() => _ = PlatformCompat.IsByRefLike(type), iterations);
        }

        _output.WriteLine(
            $"IsByRefLike x{iterations * 5}：每次取属性 {lookupTicks:N0} ticks / 缓存属性 {cachedTicks:N0} ticks → 缓存委托 {newTicks:N0} ticks");
        _output.WriteLine(
            $"加速比：vs 每次取属性 {(double)lookupTicks / Math.Max(newTicks, 1):N1}x，vs 缓存属性 {(double)cachedTicks / Math.Max(newTicks, 1):N1}x");

        // 只输出耗时对比，不断言比值：微基准受机器负载 / 频率调节影响很大，绝对值与倍数都不稳定。
        // 功能性断言在 CompatBehaviorTests.IsByRefLike_正确识别_span_与普通类型；这里只防「结果算错」。
        Assert.True(PlatformCompat.IsByRefLike(typeof(Span<int>)));
        Assert.False(PlatformCompat.IsByRefLike(type));
    }

    [Fact]
    public async Task WaitAsync_旧实现丢弃令牌_取消不生效_新实现与_BCL_一致()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        // 对照基线：令牌没接进等待链，取消后依然等到底。
        var oldWait = OldWaitAsyncIgnoringToken(pending.Task, cts.Token);
        cts.Cancel();
        Assert.False(oldWait.IsCompleted);
        pending.SetResult(true);
        Assert.True(await oldWait);

        // 现实现：取消立即结束等待，异常类型对齐 BCL Task.WaitAsync。
        var pending2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts2 = new CancellationTokenSource();
        var newWait = NetStandard20Extensions.WaitAsync(pending2.Task, cts2.Token);
        cts2.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => newWait);
        _output.WriteLine("WaitAsync：旧实现在取消后仍挂起直到内层任务完成；新实现立即抛 TaskCanceledException");
    }

    private static Task<bool> OldWaitAsyncIgnoringToken(Task<bool> task, CancellationToken cancellationToken) => task;

    private static async Task<long> MeasureAsync(Func<Task> action, int iterations)
    {
        for (var i = 0; i < 3; i++)
        {
            await action();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            await action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long Measure(Action action, int iterations)
    {
        for (var i = 0; i < 3; i++)
        {
            action();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureTicks(Action action, int iterations)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        stopwatch.Stop();
        return stopwatch.ElapsedTicks;
    }

    // ---- 以下三个方法是上一版 shim 写法的复刻，只作为 A/B 对照基线（不参与产品代码）----

    private static async Task<int> OldReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var rented = new byte[buffer.Length];
        var read = await stream.ReadAsync(rented, 0, rented.Length, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            rented.AsMemory(0, read).CopyTo(buffer);
        }

        return read;
    }

    private static Task OldWriteAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var bytes = buffer.ToArray();
        return stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    private sealed class NullStream : Stream
    {
        // 复用已完成 Task：否则 Task.FromResult(0) 每次分配都会记到 shim 的账上。
        private static readonly Task<int> Zero = Task.FromResult(0);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Zero;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
#endif
