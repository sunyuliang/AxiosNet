using System.Collections.Concurrent;
using System.Diagnostics;

namespace AxiosNet.Pipeline;

/// <summary>
/// 备用端点的健康记忆（每个客户端一份，<b>跨请求共享</b>）：连续失败到阈值后把该端点放进冷却，
/// 冷却期内它不再作为首跳；冷却到期自动重新信任，再失败则按倍数延长冷却。
/// </summary>
/// <remarks>
/// 只解决一件事：主端点持续不可用时，不要让<b>每条请求</b>都先撞它一次（那等于把 failover 的收益
/// 折成一个完整 <c>Timeout</c> 的固定开销）。判定与 <see cref="Attempt.Failover"/> 的换端点判据同源：
/// 只有「换一个下游可能好」的故障才计失败，4xx / 取消 / 配置错误既不加也不减。
/// <para>
/// 状态是<b>软上限</b>的：表按端点基址记账，超过 <see cref="MaxEntries"/> 时整体清空
/// （端点表通常是固定几个，动态改端点表才会增长；清空只是退化成「重新学一遍」，不会漏放请求）。
/// </para>
/// <para>冷却期限用单调时钟，不读墙钟：系统时间被调整不会让冷却提前结束或永不到期。</para>
/// </remarks>
internal sealed class FailoverHealth
{
    /// <summary>表内条目上限；超出时整体清空，避免动态端点表把表撑成无界常驻。</summary>
    internal const int MaxEntries = 512;

    /// <summary>一个 <see cref="Stopwatch"/> 计时单位折成 <see cref="TimeSpan"/> 刻度。</summary>
    /// <remarks>
    /// 两个频率并不总是相等（Windows 上 <see cref="Stopwatch.Frequency"/> 是 10 MHz，与
    /// <see cref="TimeSpan.TicksPerSecond"/> 相同；Linux / macOS 是 1 GHz）。直接拿
    /// <c>GetTimestamp()</c> 的值当刻度加冷却时长，会让冷却在 1 GHz 平台上缩短到千分之一。
    /// </remarks>
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
    /// 端点失败：连续失败数加一；达到 <c>failureThreshold</c> 后进入冷却，
    /// 冷却时长按超出阈值的次数翻倍（<c>baseCooldown</c> × 2^(n-阈值)），封顶 <c>maxCooldown</c>。
    /// <c>failureThreshold</c> 为 0 表示关闭健康记忆（不记状态）。
    /// </summary>
    public void RecordFailure(string key, int failureThreshold, TimeSpan baseCooldown, TimeSpan maxCooldown)
    {
        // 表过载：整体清空一次（冷路径），随后按本次失败重新学。
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
            var capped = ticks >= maxCooldown.Ticks ? maxCooldown.Ticks : (long)ticks;
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
