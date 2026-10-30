using System.Collections.Concurrent;

namespace AxiosNet.Cache;

/// <summary>
/// 内置的内存存储：进程内字典 + 到期时间 + 条目 / 字节上限，<see cref="AxiosCache"/> 默认用它。
/// </summary>
/// <remarks>
/// 每个条目自带到期时刻，读的时候顺手摘掉过期项；要收新键时先判容量（条目数与近似字节数都要放得下），
/// 满了先清过期项，仍放不下就不再接收**新**键（已存在的键照常覆盖，存活时长随之续期；
/// 不做随机淘汰 —— 命中率不会因为「刚好轮到你」而抖动，行为也可测）。
/// 带校验器（<c>ETag</c> / <c>Last-Modified</c>）的过期条目不摘：它们留给条件请求复用，
/// 由 <see cref="GetRevalidationAsync"/> 取走，直到被覆盖、被容量压力清掉或 <see cref="ClearAsync"/>。
/// 进程重启即清空；要跨进程共享请自己实现 <see cref="IAxiosCacheStore"/>。
/// </remarks>
public sealed class MemoryCacheStore : IAxiosCacheStore, IAxiosCacheRevalidationStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    // 写锁把「条目表 + 字节计量」绑成一次原子更新：SetCore 是「读旧尺寸 → 覆盖 → 记差值」，
    // 退账的路径（Remove / 读时摘除 / 清过期）若不进同一把锁，就会与它各退一次、把计量越跑越负。
    private readonly object _writeLock = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
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

    /// <summary>条目上限。</summary>
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
            if (entry.ExpiresAt > Environment.TickCount64)
            {
                return new ValueTask<AxiosShortCircuit?>(entry.Reply);
            }

            // 带校验器的过期条目留着给条件请求复用：这里只是「不命中」，不能摘。
            if (CacheValidators.HasAny(entry.Reply))
            {
                return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
            }

            // 过期顺手摘掉，不必等淘汰；只摘「刚读到的那一条」，不误删并发覆盖进来的新条目。
            // 摘除与退账必须在写锁里：否则 SetCore 会先读到这条旧尺寸，两边各退一次，计量越跑越负。
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

    private ValueTask SetCore(string key, AxiosShortCircuit entry, TimeSpan timeToLive)
    {
        var size = EstimateSize(entry);

        // 亚毫秒的存活时长按 1 毫秒算：计时器的分辨率就是毫秒。
        var ttlMs = Math.Max(1, (long)Math.Ceiling(timeToLive.TotalMilliseconds));
        var expiresAt = Environment.TickCount64 + ttlMs;

        lock (_writeLock)
        {
            // 覆盖已有键不增加条目数，所以满了也要放行 —— 否则热门键的存活时长再也不会续期。
            var exists = _entries.TryGetValue(key, out var existing);

            if (!exists && !HasRoom(size))
            {
                PurgeExpiredLocked();

                if (!HasRoom(size))
                {
                    // 满了就不收：让已有条目自然过期。
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
        // 同上：摘除与退账在写锁里做，避免与 SetCore 的「读旧尺寸 → 覆盖 → 记差值」交错成双重退账。
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

    private bool HasRoom(long size)
        => _entries.Count < _maxEntries
           && (_maxBytes == 0 || Interlocked.Read(ref _bytes) + size <= _maxBytes);

    /// <summary>在写锁内清掉全部过期条目（含带校验器的：容量压力下它们同样要让位）；清完由调用方重新判容量。</summary>
    private void PurgeExpiredLocked()
    {
        var now = Environment.TickCount64;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now
                && _entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
            {
                Interlocked.Add(ref _bytes, -pair.Value.Size);
            }
        }
    }

    /// <summary>近似尺寸：正文按字节、头按 UTF-16 字符长度 ×2 估算；不是精确的托管堆占用，只用来兜住「大响应把内存吃满」。</summary>
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

        internal AxiosShortCircuit Reply { get; }

        /// <summary>到期时刻（<see cref="Environment.TickCount64"/> 口径，单调递增）。</summary>
        internal long ExpiresAt { get; }

        /// <summary>条目的近似尺寸（<see cref="SetCore"/> 写入时算好，摘除时按它退还给字节计量）。</summary>
        internal long Size { get; }
    }
}
