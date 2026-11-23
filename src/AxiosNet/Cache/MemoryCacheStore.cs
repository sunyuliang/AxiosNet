using System.Collections.Concurrent;

namespace AxiosNet.Cache;

/// <summary>内置的内存存储：进程内字典 + 到期时间 + 条目 / 字节上限，<see cref="AxiosCache"/> 默认用它。</summary>
/// <remarks>
/// 读时顺手摘掉过期项；容量满先清过期项，仍放不下就不再接收新键（已有键照常覆盖并续期，不做随机淘汰）。
/// 带校验器的过期条目留给条件请求复用，直到被覆盖、清掉或 <see cref="ClearAsync"/>。进程重启即清空。
/// </remarks>
public sealed class MemoryCacheStore : IAxiosCacheStore, IAxiosCacheRevalidationStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    // 写锁把条目表与字节计量绑成一次原子更新。
    private readonly object _writeLock = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    // 当前近似占用字节数。
    private long _bytes;

    /// <summary>构造一份内存存储。</summary>
    /// <param name="maxEntries">条目上限，必须大于零。</param>
    /// <param name="maxBytes">近似字节上限，<c>0</c> = 不限制。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> 不大于零，或 <paramref name="maxBytes"/> 为负。</exception>
    public MemoryCacheStore(int maxEntries = 1024, long maxBytes = 64L * 1024 * 1024)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "maxEntries 必须大于零。");
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes 不能为负：不限制请设为 0。");
        }

        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    /// <summary>条目数上限；满了先清过期条目，仍无空位就不缓存新响应（不淘汰未过期条目）。</summary>
    public int MaxEntries => _maxEntries;

    /// <summary>近似字节上限；<c>0</c> 表示不限制。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>当前条目数（可能含已过期但还没被清掉的条目）。</summary>
    public int Count => _entries.Count;

    /// <summary>当前近似占用字节数（含已过期但还没被清掉的条目）。</summary>
    public long ApproximateBytes => Interlocked.Read(ref _bytes);

    /// <inheritdoc />
    public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > PlatformCompat.TickCount64())
            {
                return new ValueTask<AxiosShortCircuit?>(entry.Reply);
            }

            // 带校验器的过期条目留给条件请求复用，只是「不命中」。
            if (CacheValidators.HasAny(entry.Reply))
            {
                return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
            }

            // 过期顺手摘掉；摘除与退账须在写锁内。
            lock (_writeLock)
            {
                if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                {
                    Interlocked.Add(ref _bytes, -entry.Size);
                }
            }
        }

        return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
    }

    /// <inheritdoc />
    public ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken)
        => _entries.TryGetValue(key, out var entry) && CacheValidators.HasAny(entry.Reply)
            ? new ValueTask<AxiosShortCircuit?>(entry.Reply)
            : new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);

    /// <inheritdoc />
    public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        => SetCore(key, entry, timeToLive);

    /// <inheritdoc />
    /// <remarks>内置存储只取 <see cref="AxiosCacheEntryPolicy.TimeToLive"/>；Vary / private / stale-* 等细节留给需要它们的自建存储。</remarks>
    public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        => SetCore(key, entry, policy.TimeToLive);

    /// <summary>写入条目：算尺寸与过期时刻，腾出空间后替换同键旧条目。</summary>
    private ValueTask SetCore(string key, AxiosShortCircuit entry, TimeSpan timeToLive)
    {
        var size = EstimateSize(entry);

        // 亚毫秒的存活时长按 1 毫秒算：计时器的分辨率就是毫秒。
        var ttlMs = Math.Max(1, (long)Math.Ceiling(timeToLive.TotalMilliseconds));
        var expiresAt = PlatformCompat.TickCount64() + ttlMs;

        lock (_writeLock)
        {
            // 覆盖已有键不增加条目数：满了也放行。
            var exists = _entries.TryGetValue(key, out var existing);

            if (!exists && !HasRoom(size))
            {
                PurgeExpiredLocked();

                if (!HasRoom(size))
                {
                    return default;
                }
            }

            _entries[key] = new Entry(entry, expiresAt, size);
            Interlocked.Add(ref _bytes, size - (existing?.Size ?? 0));
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        // 摘除与退账在写锁内做。
        lock (_writeLock)
        {
            if (!_entries.TryRemove(key, out var removed))
            {
                return new ValueTask<bool>(false);
            }

            Interlocked.Add(ref _bytes, -removed.Size);
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            _entries.Clear();
            Interlocked.Exchange(ref _bytes, 0);
        }

        return default;
    }

    /// <summary>容量判定：条目数与字节预算（0 表示不限字节）都还有余量。</summary>
    private bool HasRoom(long size)
        => _entries.Count < _maxEntries
           && (_maxBytes == 0 || Interlocked.Read(ref _bytes) + size <= _maxBytes);

    /// <summary>在写锁内清掉全部过期条目（含带校验器的：容量压力下它们同样要让位）；清完由调用方重新判容量。</summary>
    private void PurgeExpiredLocked()
    {
        var now = PlatformCompat.TickCount64();
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now
                && _entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
            {
                Interlocked.Add(ref _bytes, -pair.Value.Size);
            }
        }
    }

    /// <summary>近似尺寸：正文按字节、头按 UTF-16 字符长度 ×2 估算，只用来兜住大响应。</summary>
    private static long EstimateSize(AxiosShortCircuit entry)
    {
        long size = entry.Body?.Length ?? 0;

        if (entry.Headers is { } headers)
        {
            foreach (var header in headers)
            {
                size += (header.Key.Length + header.Value.Length) * 2L;
            }
        }

        return size;
    }

    private sealed class Entry
    {
        internal Entry(AxiosShortCircuit reply, long expiresAt, long size)
        {
            Reply = reply;
            ExpiresAt = expiresAt;
            Size = size;
        }

        /// <summary>缓存下来的短路响应（正文已缓冲，可直接复用）。</summary>
        internal AxiosShortCircuit Reply { get; }

        /// <summary>到期时刻（<see cref="PlatformCompat.TickCount64()"/> 口径，单调递增）。</summary>
        internal long ExpiresAt { get; }

        /// <summary>条目的近似尺寸（<see cref="SetCore"/> 写入时算好，摘除时按它退还给字节计量）。</summary>
        internal long Size { get; }
    }
}
