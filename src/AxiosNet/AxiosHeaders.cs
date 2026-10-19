using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、支持多值、支持链式调用。
/// <para>
/// 所有读写都在内部锁内完成，因此实例默认值（<see cref="Axios.Defaults"/> 等）可以在
/// 请求进行中安全地增删请求头（例如运行期刷新 Token），不会让并发的配置合并看到半成品集合。
/// 枚举与 <see cref="Clone"/> 返回快照，不持有实时视图。
/// </para>
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _store
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    public AxiosHeaders()
    {
    }

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kv in headers)
        {
            _store[NormalizeName(kv.Key)] = kv.Value;
        }
    }

    /// <summary>设置/覆盖请求头，返回当前对象以支持链式调用。</summary>
    public AxiosHeaders Set(string name, string? value)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace 是 .NET 8 才有的 API，这里手写以兼容 net6.0 目标。
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", nameof(name));
        }
        var key = NormalizeName(name);
        lock (_sync)
        {
            if (value is null)
            {
                _store.Remove(key);
            }
            else
            {
                _store[key] = value;
            }
        }

        return this;
    }

    /// <summary>批量合并请求头。</summary>
    public AxiosHeaders Set(IEnumerable<KeyValuePair<string, string?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kv in headers)
        {
            Set(kv.Key, kv.Value);
        }

        return this;
    }

    public string? Get(string name)
    {
        lock (_sync)
        {
            return _store.TryGetValue(NormalizeName(name), out var value) ? value : null;
        }
    }

    public bool Has(string name)
    {
        lock (_sync)
        {
            return _store.ContainsKey(NormalizeName(name));
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            return _store.Remove(NormalizeName(name));
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _store.Clear();
        }
    }

    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _store.Count;
            }
        }
    }

    public AxiosHeaders Clone() => new(Snapshot());

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        // 先取快照，避免两个实例互相合并时持锁顺序相反造成死锁。
        var snapshot = other.Snapshot();
        lock (_sync)
        {
            foreach (var kv in snapshot)
            {
                _store[kv.Key] = kv.Value;
            }
        }
    }

    private static string NormalizeName(string name) => name.Trim();

    private List<KeyValuePair<string, string>> Snapshot()
    {
        lock (_sync)
        {
            return new List<KeyValuePair<string, string>>(_store);
        }
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
