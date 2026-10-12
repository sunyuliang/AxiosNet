using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、支持多值、支持链式调用。
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _store
        = new(StringComparer.OrdinalIgnoreCase);

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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var key = NormalizeName(name);
        if (value is null)
        {
            _store.Remove(key);
        }
        else
        {
            _store[key] = value;
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
        => _store.TryGetValue(NormalizeName(name), out var value) ? value : null;

    public bool Has(string name) => _store.ContainsKey(NormalizeName(name));

    public bool Delete(string name) => _store.Remove(NormalizeName(name));

    public void Clear() => _store.Clear();

    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    public int Count => _store.Count;

    public AxiosHeaders Clone() => new(this);

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        foreach (var kv in other._store)
        {
            _store[kv.Key] = kv.Value;
        }
    }

    private static string NormalizeName(string name) => name.Trim();

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _store.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
