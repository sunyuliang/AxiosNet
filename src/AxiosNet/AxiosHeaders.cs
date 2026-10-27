using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、同名覆盖（单值）、支持链式调用。
/// 实例持有一个不可变的条目数组（条目内含查找用判别位），写入 = 算出新数组 + 原子换上，读与克隆都无锁。
/// 写入即校验：头名必须是 RFC 7230 token，取值不允许 <c>CR</c> / <c>LF</c> 等控制字符。
/// 读/删入口对 <c>null</c> 名称抛 <see cref="ArgumentNullException"/>。
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    /// <summary>
    /// 一个头条目（不可变）。<see cref="Meta"/> 是查找用的判别位：
    /// <c>(长度 &lt;&lt; 16) | (首字符 &lt;&lt; 8) | 末字符</c>。
    /// </summary>
    internal readonly struct Entry
    {
        internal readonly string Name;
        internal readonly string Value;
        internal readonly int Meta;

        internal Entry(string name, string value)
        {
            Name = name;
            Value = value;
            Meta = MetaOf(name);
        }

        /// <summary>判别位：首尾字符按 <b>ASCII 大写</b>折叠后打包；<paramref name="name"/> 不能为空。</summary>
        internal static int MetaOf(string name)
        {
            var length = name.Length;
            return (length << 16) | (Fold(name[0]) << 8) | Fold(name[length - 1]);
        }

        private static int Fold(char c) => c is >= 'a' and <= 'z' ? c - ('a' - 'A') : c;
    }

    /// <summary>空数组单例：绝大多数 headers 是空的（懒创建的 <c>Config.Headers</c>）。</summary>
    private static readonly Entry[] Empty = Array.Empty<Entry>();

    /// <summary>当前版本：<see cref="Volatile"/> 读，<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 写。</summary>
    private Entry[] _items;

    public AxiosHeaders() => _items = Empty;

    private AxiosHeaders(Entry[] items) => _items = items;

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // 先在 List 里攒、最后一次性成型，避免逐条 CAS 复制 N 次数组。
        var capacity = headers is ICollection<KeyValuePair<string, string>> known ? known.Count : 4;
        var buffer = new List<Entry>(capacity);

        foreach (var kv in headers)
        {
            var name = ValidateName(kv.Key);

            // null 取值等价于删除：删一个不存在的条目就是空操作。
            if (kv.Value is null)
            {
                Remove(buffer, name);
                continue;
            }

            ValidateHeaderValue(name, kv.Value);
            Put(buffer, name, kv.Value);
        }

        _items = buffer.Count == 0 ? Empty : buffer.ToArray();
    }

    /// <summary>设置/覆盖请求头，返回当前对象以支持链式调用。<c>null</c> 取值等价于删除。</summary>
    /// <exception cref="ArgumentException">名称不是合法 HTTP token，或取值含控制字符。</exception>
    public AxiosHeaders Set(string name, string? value)
    {
        var key = ValidateName(name);

        // null 表示删除：直接走 Delete，不校验取值。
        if (value is null)
        {
            Delete(key);
            return this;
        }

        ValidateHeaderValue(key, value);
        Publish(key, value);
        return this;
    }

    /// <summary>批量合并请求头（逐条校验、逐条生效，第 k 条非法时前 k-1 条已写入）。</summary>
    public AxiosHeaders Set(IEnumerable<KeyValuePair<string, string?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // 先物化：CAS 重试不能重复枚举一次性序列，且校验失败时已通过的前缀照常生效。
        var ops = new List<(string Name, string? Value)>(4);
        try
        {
            foreach (var kv in headers)
            {
                var name = ValidateName(kv.Key);

                // null 等价删除（不校验取值），其余一律过 CR/LF 闸口。
                if (kv.Value is not null)
                {
                    ValidateHeaderValue(name, kv.Value);
                }

                ops.Add((name, kv.Value));
            }
        }
        catch
        {
            Apply(ops);
            throw;
        }

        Apply(ops);
        return this;
    }

    /// <summary>读取请求头（大小写不敏感，名称允许带首尾空白）。不存在返回 <c>null</c>。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var items = Volatile.Read(ref _items);
        var index = IndexOf(items, name);
        if (index >= 0)
        {
            return items[index].Value;
        }

        // 兼容「名称带首尾空白」：只在未命中时才 Trim 重试一次。
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length == name.Length)
        {
            return null;
        }

        index = IndexOf(items, trimmed);
        return index >= 0 ? items[index].Value : null;
    }

    /// <summary>指定名称是否存在（大小写不敏感）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var items = Volatile.Read(ref _items);
        if (IndexOf(items, name) >= 0)
        {
            return true;
        }

        var trimmed = name.Trim();
        return trimmed.Length != 0 && trimmed.Length != name.Length && IndexOf(items, trimmed) >= 0;
    }

    /// <summary>删除指定请求头，返回是否真的删掉了条目。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var key = NormalizeName(name);
        if (key.Length == 0)
        {
            return false;
        }

        while (true)
        {
            var current = Volatile.Read(ref _items);
            var index = IndexOf(current, key);

            // 不存在就不发布新版本。
            if (index < 0)
            {
                return false;
            }

            var next = new Entry[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return true;
            }
        }
    }

    /// <summary>清空：无条件发布空版本。</summary>
    /// <remarks>
    /// 同 <see cref="Internal.SynchronizedList{T}.Clear"/>：盲写单例 <see cref="Empty"/>，没有「读-改-写」就没有丢更新，
    /// 与并发 <see cref="Set(string, string?)"/> / <see cref="Delete"/> 的任意交错都可线性化。改成 CAS 循环是等价改写，不要为了「统一」而动。
    /// </remarks>
    public void Clear()
    {
        // 空版本没有内容可清，不发布也不分配。
        if (Volatile.Read(ref _items).Length == 0)
        {
            return;
        }

        Volatile.Write(ref _items, Empty);
    }

    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    /// <summary>条目数，无锁读（数组长度）。</summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>写时复制：不复制条目、不重新校验，新实例指向同一版数组。</summary>
    public AxiosHeaders Clone() => new(Volatile.Read(ref _items));

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        // 取对方当前版本：不可变数组本身就是一致快照。
        var incoming = Volatile.Read(ref other._items);
        if (incoming.Length == 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _items);

            // 合并结果没变化就不发布新版本。
            if (IsUnchanged(current, incoming))
            {
                return;
            }

            var next = Merge(current, incoming);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
            }
        }
    }

    /// <summary>当前版本数组的快照（零拷贝），调用方<b>不得修改</b>。</summary>
    internal Entry[] Snapshot() => Volatile.Read(ref _items);

    /// <summary>结构体枚举器：<c>foreach</c> 不装箱，进入枚举时取定快照。</summary>
    public Enumerator GetEnumerator() => new(Volatile.Read(ref _items));

    IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>只读结构体枚举器，基于 <see cref="Snapshot"/> 的数组。</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<string, string>>
    {
        private readonly Entry[] _items;
        private int _index;

        internal Enumerator(Entry[] items)
        {
            _items = items;
            _index = -1;
        }

        public KeyValuePair<string, string> Current => new(_items[_index].Name, _items[_index].Value);

        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _items.Length;

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }

    /// <summary>单条写入：算出新数组后 CAS 发布；同值重设不发布、不分配。</summary>
    private void Publish(string name, string value)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);
            var index = IndexOf(current, name);

            if (index >= 0 && string.Equals(current[index].Value, value, StringComparison.Ordinal))
            {
                return;
            }

            var next = new Entry[index >= 0 ? current.Length : current.Length + 1];
            Array.Copy(current, next, current.Length);

            if (index >= 0)
            {
                next[index] = new Entry(name, value);
            }
            else
            {
                next[current.Length] = new Entry(name, value);
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
            }
        }
    }

    /// <summary>批量应用：一次成型、只发布一次，避免逐条 CAS 的 O(N²) 复制。</summary>
    private void Apply(List<(string Name, string? Value)> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _items);
            var next = ApplyOps(current, ops);

            // 全是幂等写时 ApplyOps 原样返回 current。
            if (ReferenceEquals(next, current)
                || ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
            }
        }
    }

    private static Entry[] ApplyOps(Entry[] current, List<(string Name, string? Value)> ops)
    {
        // 先过一遍：有没有实际变化、要新增几条（覆盖与删除不改变长度），一次分配到位。
        var added = 0;
        var changed = false;
        for (var i = 0; i < ops.Count; i++)
        {
            var (name, value) = ops[i];
            var index = IndexOf(current, name);

            if (index < 0)
            {
                if (value is not null)
                {
                    added++;
                    changed = true;
                }

                continue;
            }

            if (!string.Equals(current[index].Value, value, StringComparison.Ordinal))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return current;
        }

        var next = new List<Entry>(current.Length + added);
        next.AddRange(current);

        for (var i = 0; i < ops.Count; i++)
        {
            var (name, value) = ops[i];
            if (value is null)
            {
                Remove(next, name);
                continue;
            }

            Put(next, name, value);
        }

        return next.Count == 0 ? Empty : next.ToArray();
    }

    /// <summary>当前版本是否已经等于「并入 <paramref name="incoming"/> 之后」的结果（用于免发布）。</summary>
    private static bool IsUnchanged(Entry[] current, Entry[] incoming)
    {
        for (var i = 0; i < incoming.Length; i++)
        {
            var index = IndexOf(current, incoming[i].Name);
            if (index < 0 || !string.Equals(current[index].Value, incoming[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 <paramref name="incoming"/> 覆盖合并进 <paramref name="current"/>，产出一版新数组。</summary>
    private static Entry[] Merge(Entry[] current, Entry[] incoming)
    {
        var added = 0;
        for (var i = 0; i < incoming.Length; i++)
        {
            if (IndexOf(current, incoming[i].Name) < 0)
            {
                added++;
            }
        }

        var merged = new List<Entry>(current.Length + added);
        merged.AddRange(current);

        for (var i = 0; i < incoming.Length; i++)
        {
            Put(merged, incoming[i].Name, incoming[i].Value);
        }

        return merged.ToArray();
    }

    /// <summary>写入（覆盖同名、否则追加）。构造期与批量合并用，O(N) 扫描。</summary>
    private static void Put(List<Entry> items, string name, string value)
    {
        var index = IndexOf(items, name);
        if (index >= 0)
        {
            items[index] = new Entry(name, value);
            return;
        }

        items.Add(new Entry(name, value));
    }

    private static void Remove(List<Entry> items, string name)
    {
        var index = IndexOf(items, name);
        if (index >= 0)
        {
            items.RemoveAt(index);
        }
    }

    private static int IndexOf(Entry[] items, string name)
    {
        if (name.Length == 0)
        {
            return -1;
        }

        var meta = Entry.MetaOf(name);
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Meta == meta && string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(List<Entry> items, string name)
    {
        if (name.Length == 0)
        {
            return -1;
        }

        var meta = Entry.MetaOf(name);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Meta == meta && string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeName(string name) => name.Trim();

    /// <summary>写入前的名称校验：RFC 7230 token（RFC 9110 §5.1 同）。</summary>
    private static string ValidateName(string name)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace 是 .NET 8 才有的 API，这里手写以兼容 net6.0 目标。
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", nameof(name));
        }

        // 先按原始串拒绝控制字符（含 CR/LF）：尾部的 \r\n 会被 Trim 悄悄吃掉，那正是注入尝试的痕迹。
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException($"非法 HTTP 请求头名称（含控制字符）：'{Escape(name)}'。", nameof(name));
            }
        }

        var trimmed = name.Trim();
        foreach (var c in trimmed)
        {
            if (!IsTokenChar(c))
            {
                throw new ArgumentException($"非法 HTTP 请求头名称 '{name}'：只允许 RFC 7230 token 字符（字母/数字与 !#$%&'*+-.^_`|~），" + "不允许空格、冒号或控制字符。", nameof(name));
            }
        }

        return trimmed;
    }

    /// <summary>把控制字符转义成可见形式，便于错误信息与日志排查。</summary>
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                builder.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>取值校验：拒绝 <b>C0 控制字符</b>（HTAB 除外）与 DEL，防止 CR/LF 注入（CWE-113）。</summary>
    /// <exception cref="ArgumentException">值里含被禁字符。</exception>
    internal static void ValidateHeaderValue(string name, string value)
    {
        foreach (var c in value)
        {
            if (c == '\t')
            {
                continue;
            }

            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException($"非法 HTTP 请求头取值（头 '{name}'）：不允许包含控制字符（这里是 U+{(int)c:X4}）。" + "CR / LF 会被当成头部分隔符，等于允许调用方追加任意头或请求体；" + "如果这是来自外部的数据，请先剔除控制字符——否则可能被用于注入额外的请求头或请求体。", "value");
            }
        }
    }

    /// <summary>头名校验的共用入口：RFC 7230 token，返回去首尾空白后的名称。</summary>
    /// <exception cref="ArgumentException">名称为空、含控制字符或非 token 字符。</exception>
    internal static string ValidateHeaderName(string name) => ValidateName(name);

    /// <summary>RFC 7230 token 字符（头名的字符集）。</summary>
    private static bool IsTokenChar(char c)
        => (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
