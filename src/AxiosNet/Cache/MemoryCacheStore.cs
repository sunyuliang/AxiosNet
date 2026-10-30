using System.Collections.Concurrent;

namespace AxiosNet.Cache;

/// <summary>
/// 内置的内存存储：进程内字典 + 到期时间 + 条目上限，<see cref="AxiosCache"/> 默认用它。
/// </summary>
/// <remarks>
/// 每个条目自带到期时刻，读的时候顺手摘掉过期项；条目满了先清过期项，仍满就不再接收**新**键
/// （已存在的键照常覆盖，存活时长随之续期；不做随机淘汰 —— 命中率不会因为「刚好轮到你」而抖动，行为也可测）。
/// 进程重启即清空；要跨进程共享请自己实现 <see cref="IAxiosCacheStore"/>。
/// </remarks>
public sealed class MemoryCacheStore : IAxiosCacheStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    /// <summary>构造一份内存存储。<paramref name="maxEntries"/> 是条目上限，必须大于零。</summary>
    public MemoryCacheStore(int maxEntries = 1024)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "maxEntries 必须大于零。");
        }

        _maxEntries = maxEntries;
    }

    /// <summary>条目上限。</summary>
    public int MaxEntries => _maxEntries;

    /// <summary>当前条目数（可能含已过期但还没被清掉的条目）。</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > Environment.TickCount64)
            {
                return new ValueTask<AxiosShortCircuit?>(entry.Reply);
            }

            // 过期顺手摘掉，不必等淘汰。
            _entries.TryRemove(key, out _);
        }

        return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        // 覆盖已有键不增加条目数，所以满了也要放行 —— 否则热门键的存活时长再也不会续期。
        if (!_entries.ContainsKey(key) && _entries.Count >= _maxEntries)
        {
            PurgeExpired();

            if (_entries.Count >= _maxEntries)
            {
                // 满了就不收：让已有条目自然过期。
                return default;
            }
        }

        // 亚毫秒的存活时长按 1 毫秒算：计时器的分辨率就是毫秒。
        var ttlMs = Math.Max(1, (long)Math.Ceiling(timeToLive.TotalMilliseconds));
        _entries[key] = new Entry(entry, Environment.TickCount64 + ttlMs);
        return default;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
        => new(_entries.TryRemove(key, out _));

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        _entries.Clear();
        return default;
    }

    private void PurgeExpired()
    {
        var now = Environment.TickCount64;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private readonly struct Entry
    {
        internal Entry(AxiosShortCircuit reply, long expiresAt)
        {
            Reply = reply;
            ExpiresAt = expiresAt;
        }

        internal AxiosShortCircuit Reply { get; }

        /// <summary>到期时刻（<see cref="Environment.TickCount64"/> 口径，单调递增）。</summary>
        internal long ExpiresAt { get; }
    }
}
