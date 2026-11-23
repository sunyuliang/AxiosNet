using System.Collections.Concurrent;
using System.Diagnostics;

namespace AxiosNet.Pipeline;

/// <summary>备用端点的健康记忆（每个客户端一份，跨请求共享）：连续失败到阈值后进入冷却，冷却到期自动重新信任，再失败按倍数延长。</summary>
/// <remarks>
/// 判定与 <see cref="Attempt.Failover"/> 的换端点判据同源：只有「换一个下游可能好」的故障才计失败，4xx / 取消 / 配置错误既不加也不减。
/// 表按端点基址记账，超过 <see cref="MaxEntries"/> 时整体清空；冷却用单调时钟，系统时间调整不影响到期。
/// </remarks>
internal sealed class FailoverHealth
{
    /// <summary>表内条目上限；超出时整体清空。</summary>
    internal const int MaxEntries = 512;

    /// <summary>一个 <see cref="Stopwatch"/> 计时单位折成 <see cref="TimeSpan"/> 刻度。</summary>
    /// <remarks>两个频率并不总是相等（Windows 为 10 MHz，Linux / macOS 为 1 GHz），不能直接把 <c>GetTimestamp()</c> 的值当刻度用。</remarks>
    private static readonly double TicksPerTimestamp = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>单调时钟，单位是 <see cref="TimeSpan"/> 刻度。</summary>
    private readonly Func<long> _clock;

    public FailoverHealth()
        : this(clock: null)
    {
    }

    /// <summary>测试用：注入单调时钟（单位 <see cref="TimeSpan"/> 刻度）以控制冷却窗口。</summary>
    internal FailoverHealth(Func<long>? clock) => _clock = clock ?? NowTicks;

    /// <summary>当前单调时钟（已换算成 <see cref="TimeSpan"/> 刻度），不受系统时间调整影响。</summary>
    private static long NowTicks() => (long)(Stopwatch.GetTimestamp() * TicksPerTimestamp);

    /// <summary>该端点此刻是否可用：没有记录、连续失败未达阈值、或冷却已到期都算可用。</summary>
    public bool IsAvailable(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return true;
        }

        lock (entry)
        {
            // CooldownUntil == 0 表示当前没有冷却；非零时比单调时钟。
            return entry.CooldownUntil == 0 || _clock() >= entry.CooldownUntil;
        }
    }

    /// <summary>端点给出了一次成功证据（含 4xx）：清零连续失败与冷却。</summary>
    public void RecordSuccess(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }

        lock (entry)
        {
            entry.ConsecutiveFailures = 0;
            entry.CooldownUntil = 0;
        }
    }

    /// <summary>
    /// 端点失败：连续失败数加一，达到 <c>failureThreshold</c> 后进入冷却，时长按 <c>baseCooldown</c> × 2^(n-阈值) 翻倍并封顶 <c>maxCooldown</c>；
    /// <c>failureThreshold</c> 为 0 表示关闭健康记忆。
    /// </summary>
    public void RecordFailure(string key, int failureThreshold, TimeSpan baseCooldown, TimeSpan maxCooldown)
    {
        // 表过载：整体清空。
        if (_entries.Count > MaxEntries)
        {
            _entries.Clear();
        }

        // 关闭态不记状态。
        if (failureThreshold <= 0 || baseCooldown <= TimeSpan.Zero)
        {
            return;
        }

        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        lock (entry)
        {
            entry.ConsecutiveFailures++;

            if (entry.ConsecutiveFailures < failureThreshold)
            {
                return;
            }

            var doublings = entry.ConsecutiveFailures - failureThreshold;
            var ticks = (double)baseCooldown.Ticks * Math.Pow(2d, Math.Min(doublings, 16));

            // 上限低于基础冷却时按基础冷却处理（FailoverSettings.MaxCooldown 的约定）。
            var cap = Math.Max(baseCooldown.Ticks, maxCooldown.Ticks);
            var capped = ticks >= cap ? cap : (long)ticks;
            entry.CooldownUntil = _clock() + Math.Max(1L, capped);
        }
    }

    /// <summary>单个端点的健康状态；用独立锁，失败与读取互不阻塞其他端点。</summary>
    private sealed class Entry
    {
        public int ConsecutiveFailures;

        public long CooldownUntil;
    }
}
