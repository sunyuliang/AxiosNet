using System.Diagnostics;
using AxiosNet.Comparison.Targets;

namespace AxiosNet.Comparison.Harness;

/// <summary>
/// 一个测量窗口的执行器：<paramref name="Concurrency"/> 个 worker 常驻，窗口内不停发请求，
/// 采集吞吐、客户端观察延迟与进程分配。
/// <para>
/// 停窗用「置停止位 + 等在途请求自然收尾」而不是取消令牌：取消会把连接打成异常关闭，
/// 下一轮要重新建连，被污染的是下一次测量。
/// </para>
/// </summary>
internal sealed class LoadRunner
{
    /// <summary>每个 worker 记录的延迟样本上限。1 秒窗口内单个 worker 远超此数说明本机已不是瓶颈，直接截断即可。</summary>
    private const int MaxSamplesPerWorker = 1 << 16;

    /// <summary>
    /// 全部 worker 的样本总预算（8 MB，long 数组）：每 worker 上限按并发摊分，
    /// 避免 `--concurrency 512` 时 512 × 512 KB 的样本数组把测量内存压到 JIT/GC 上。
    /// 超出上限只会截断延迟统计，吞吐与分配不受影响（报告里有 dropped 提示）。
    /// </summary>
    private const int TotalSampleBudget = 1 << 23;

    private readonly int _concurrency;

    public LoadRunner(int concurrency)
    {
        if (concurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency), "并发必须大于 0。");
        }

        _concurrency = concurrency;
    }

    public async Task<WindowResult> RunAsync(ITarget target, TimeSpan window)
    {
        var sessions = new ITargetSession[_concurrency];
        var samples = new long[_concurrency][];
        var perWorkerSamples = Math.Clamp(TotalSampleBudget / _concurrency, 1024, MaxSamplesPerWorker);

        try
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                sessions[i] = target.OpenSession();
                samples[i] = new long[perWorkerSamples];
            }

            return await MeasureAsync(target.Name, sessions, samples, window).ConfigureAwait(false);
        }
        finally
        {
            foreach (var session in sessions)
            {
                session?.Dispose();
            }
        }
    }

    private async Task<WindowResult> MeasureAsync(
        string name,
        ITargetSession[] sessions,
        long[][] samples,
        TimeSpan window)
    {
        var counts = new int[sessions.Length];
        var errors = 0;
        Exception? firstError = null;
        var stop = 0;

        // 每窗从同一个干净堆起跑：分配指标是「总分配」，谁都不背上一窗的垃圾。
        CollectGarbage();

        using var guard = new CancellationTokenSource(window + TimeSpan.FromSeconds(60));
        using var process = Process.GetCurrentProcess();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();

        var workers = new Task[sessions.Length];
        for (var i = 0; i < workers.Length; i++)
        {
            var index = i;
            workers[i] = Task.Run(async () =>
            {
                var session = sessions[index];
                var buffer = samples[index];
                var count = 0;

                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        var started = Stopwatch.GetTimestamp();
                        try
                        {
                            await session.SendAsync(guard.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errors);
                            Interlocked.CompareExchange(ref firstError, ex, null);
                            return;
                        }

                        // 窗口收尾后才完成的在途请求不算进吞吐（否则停窗后的 Drain 会被记成「白捡的请求」）
                        if (Volatile.Read(ref stop) != 0)
                        {
                            return;
                        }

                        var elapsed = Stopwatch.GetTimestamp() - started;
                        if (count < buffer.Length)
                        {
                            buffer[count] = elapsed;
                        }

                        count++;
                    }
                }
                finally
                {
                    counts[index] = count;
                }
            });
        }

        await Task.Delay(window).ConfigureAwait(false);
        var seconds = clock.Elapsed.TotalSeconds;

        Volatile.Write(ref stop, 1);
        await Task.WhenAll(workers).ConfigureAwait(false);

        // 分配在读排序之前取样：排序自己的分配不该记到被测实现头上。
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        process.Refresh();
        var cpuShare = (process.TotalProcessorTime - cpuBefore).TotalSeconds
            / seconds
            / Environment.ProcessorCount;

        var completed = 0L;
        foreach (var count in counts)
        {
            completed += count;
        }

        var all = new long[completed];
        var recorded = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var length = Math.Min(counts[i], samples[i].Length);
            Array.Copy(samples[i], 0, all, recorded, length);
            recorded += length;
        }

        Array.Sort(all, 0, recorded);

        return new WindowResult(
            name,
            seconds,
            completed,
            errors,
            firstError,
            completed / seconds,
            PercentileMicroseconds(all, recorded, 0.50),
            PercentileMicroseconds(all, recorded, 0.99),
            PercentileMicroseconds(all, recorded, 1.0),
            completed > 0 ? (double)allocated / completed : 0,
            cpuShare,
            (int)(completed - recorded));
    }

    /// <summary>第 p 分位（p ∈ (0,1]），单位微秒。空样本返回 0。</summary>
    private static double PercentileMicroseconds(long[] sortedTicks, int count, double p)
    {
        if (count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(p * count) - 1;
        index = Math.Clamp(index, 0, count - 1);
        return sortedTicks[index] * 1_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>每窗前各压一次 GC：让每个实现都从同一个干净堆起跑。</summary>
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

/// <summary>一个测量窗口的结果。延迟为客户端观察值（含服务端处理 + 回环 RTT + 反序列化）。</summary>
internal readonly record struct WindowResult(
    string Target,
    double Seconds,
    long Completed,
    int Errors,
    Exception? FirstError,
    double Rps,
    double P50Microseconds,
    double P99Microseconds,
    double MaxMicroseconds,
    double AllocatedBytesPerRequest,
    double CpuShare,
    int SamplesDropped);
