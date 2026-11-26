using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.Internal.Url;

/// <summary>按键缓存的小表：条目数达上限时整表清空，不给淘汰算法留状态。</summary>
/// <remarks>
/// 上限是软上限——并发下「计数已满」与「插入成功」之间可能多进几条，最坏略超上限。三条路径都不分配闭包：
/// 取用是纯字典查找，写入只在未命中时发生。
/// </remarks>
/// <typeparam name="TValue">缓存的值；引用类型与结构体都支持。</typeparam>
internal sealed class BoundedCache<TValue>
{
    private readonly ConcurrentDictionary<string, TValue> _entries;
    private readonly int _limit;
    private int _count;

    /// <summary>建一张按键缓存。</summary>
    /// <param name="limit">条目上限，达到后整表清空。</param>
    internal BoundedCache(int limit)
    {
        _entries = new ConcurrentDictionary<string, TValue>(StringComparer.Ordinal);
        _limit = limit;
    }

    /// <summary>当前条目数（要遍历全部桶，只供诊断与测试读取，不要放进请求路径）。</summary>
    internal int Count => _entries.Count;

    /// <summary>取一条；未命中返回 <c>false</c> 且 <paramref name="value"/> 为默认值。</summary>
    /// <param name="key">键（区分大小写）。</param>
    /// <param name="value">命中的值。</param>
    /// <returns>是否命中。</returns>
    internal bool TryGet(string key, [NotNullWhen(true)] out TValue? value)
    {
        if (_entries.TryGetValue(key, out var stored))
        {
            // 字典的 out 参数按 MaybeNull 标注，命中时实际非空。
            value = stored!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>写入一条；达上限先整表清空。同键已存在时保留旧值，不覆盖。</summary>
    /// <param name="key">键（区分大小写）。</param>
    /// <param name="value">要缓存的值。</param>
    internal void Add(string key, TValue value)
    {
        if (Volatile.Read(ref _count) >= _limit)
        {
            _entries.Clear();
            Volatile.Write(ref _count, 0);
        }

        // 只有真的插进去才计数。
        if (_entries.TryAdd(key, value))
        {
            Interlocked.Increment(ref _count);
        }
    }
}
