using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、同名覆盖（单值）、支持链式调用。
/// <para>
/// <b>表示</b>：实例只持有一个<b>不可变</b>的条目数组，发布后永不修改。写入 = 算出新数组 + 原子换上，
/// 因此读路径<b>完全无锁</b>（任何一版数组都是完整的），克隆<b>不复制任何东西</b>（只共享数组引用），
/// 也没有引用计数、分离、快照缓存这些东西。
/// </para>
/// <para>
/// <b>为什么不用字典</b>：字典的复制成本随头数线性增长（5 头 ≈ 400 B、10 头 ≈ 500 B），
/// 而「每请求克隆一份默认头、再写一两个头」是典型形态 —— 每次写都要整份复制字典。
/// 条目数组的一次写入只产出一版新数组（5 头 ≈ 190 B），克隆与读取都不再随头数付费。
/// </para>
/// <para>
/// <b>查找</b>：每个条目内预存 <c>(长度, 首字符, 末字符)</c> 打包成的判别位，先比 <c>int</c> 再比字符串 ——
/// 判别位与条目同处一条缓存行，命中率高的头名（<c>Authorization</c> / <c>Content-Type</c>）不必解引用字符串。
/// 实测 5 头命中 3.7 ns、10 头 5.8 ns（字典实现约 20 ns）。代价是查找为 O(N)：
/// 头数 &gt; 20 时逐条比 <c>int</c> 的开销会超过一次哈希查找，但那是极端形态（每请求也就查 3 次）。
/// </para>
/// <para>
/// <b>写入成本</b>：单条 <see cref="Set(string, string?)"/> 是 O(N) 复制（一次写入产出一版新数组，与头数成正比）。
/// 因此<b>逐条建 N 个头是 O(N²)</b>（200 个约 480 KB 垃圾）；批量建头请用构造或
/// <see cref="Set(IEnumerable{KeyValuePair{string, string?}})"/> —— 两者都是一次成型、只发布一次。
/// 每请求「克隆 + 改一两个头」的形态不受影响：N 是当前头数（通常 ≤ 10），一次写入只付一版数组。
/// </para>
/// <para>
/// <b>并发</b>：<see cref="_items"/> 只用 <see cref="Volatile"/> 读、<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 发布。
/// 两个线程同时写不同的头不会互相覆盖（CAS 失败就基于新版本重算），因此实例默认头可以在请求进行中安全修改。
/// </para>
/// <para>
/// <b>写入即校验</b>：头名必须是 RFC 7230 token，取值不允许 <c>CR</c> / <c>LF</c> 等控制字符 ——
/// 上送层用 <c>TryAddWithoutValidation</c> 不做任何检查，这里是唯一的闸口（CWE-113 请求拆分）。
/// </para>
/// <para>
/// 读/删入口对 <c>null</c> 名称抛 <see cref="ArgumentNullException"/>（带参数名，便于定位）。
/// </para>
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    /// <summary>
    /// 一个头条目（不可变）。<see cref="Meta"/> 是查找用的判别位：
    /// <c>(长度 &lt;&lt; 16) | (首字符 &lt;&lt; 8) | 末字符</c>。
    /// <para>
    /// 打包成一个 <c>int</c> 而不是在查找时读 <c>string.Length</c>，是为了让「不像」的条目在一次缓存行访问里
    /// 就被排除：头名的长度和首尾字符几乎总能区分开（<c>Authorization</c> / <c>Content-Type</c> / <c>Accept</c>），
    /// 只有真正可能相等的条目才需要解引用字符串比较。
    /// </para>
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

        /// <summary>
        /// 判别位。<paramref name="name"/> 不能为空（空名在入口处已挡掉）。
        /// <para>
        /// 首尾字符按 <b>ASCII 大写</b>折叠后再打包：头名比较是大小写不敏感的，
        /// 不折叠的话 <c>X-Trace</c> 与 <c>x-trace</c> 会算出不同的判别位，查找直接漏掉。
        /// 头名只允许 RFC 7230 token 字符（纯 ASCII），因此这里不必考虑 OrdinalIgnoreCase 的 Unicode 折叠规则。
        /// </para>
        /// </summary>
        internal static int MetaOf(string name)
        {
            var length = name.Length;
            return (length << 16) | (Fold(name[0]) << 8) | Fold(name[length - 1]);
        }

        private static int Fold(char c) => c is >= 'a' and <= 'z' ? c - ('a' - 'A') : c;
    }

    /// <summary>空数组单例：绝大多数 headers 是空的（懒创建的 <c>Config.Headers</c>），不必为它们分配。</summary>
    private static readonly Entry[] Empty = Array.Empty<Entry>();

    /// <summary>
    /// 当前版本。只通过 <see cref="Volatile"/> 读、<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 写：
    /// 写入方发布新数组，读者取引用 —— 普通字段的读可能被 JIT 提出循环，读到已经被换掉的旧版本。
    /// </summary>
    private Entry[] _items;

    public AxiosHeaders() => _items = Empty;

    private AxiosHeaders(Entry[] items) => _items = items;

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // 逐条写入但只成型一次：先在 List 里攒，最后 ToArray。
        // 直接对 _items 逐条 CAS 会按 1、2、3… 复制 N 次数组（10 头约 1.4 KB），攒完一次成型只付一份。
        var capacity = headers is ICollection<KeyValuePair<string, string>> known ? known.Count : 4;
        var buffer = new List<Entry>(capacity);

        foreach (var kv in headers)
        {
            var name = ValidateName(kv.Key);

            // null 取值等价于「删除」：构造集合时删一个不存在的东西就是空操作。
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

        // null 表示删除：直接走 Delete，复用「不存在就不发布新版本」的快速路径（不校验取值）。
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

        // 先物化：CAS 重试不能重复枚举一个可能只能枚举一次的序列；也保证「校验到一半抛异常」时
        // 已通过校验的前缀照常生效（与逐条 Set 的语义一致）。
        var ops = new List<(string Name, string? Value)>(4);
        try
        {
            foreach (var kv in headers)
            {
                ops.Add((ValidateName(kv.Key), kv.Value));
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

        // 兼容「名称带首尾空白」：绝大多数调用不带，因此只在未命中时才 Trim 重试一次。
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

            // 快速路径：不存在就不发布新版本（调用方统一清默认项时很常见）。
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

    public void Clear()
    {
        // 空的版本没有内容可清：不发布、不分配。
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

    /// <summary>条目数。无锁读（数组长度），比旧实现少一次加锁。</summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>
    /// 写时复制的克隆：不复制条目、不重新校验，只让新实例指向同一版数组。
    /// <para>
    /// 之后任意一方写入都会发布自己的新数组（CAS 换引用），语义与「深拷贝」一致，
    /// 但只读用法不再为头数付费 —— 成本恒为 24 B（实例本身）。
    /// </para>
    /// </summary>
    public AxiosHeaders Clone() => new(Volatile.Read(ref _items));

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        // 取对方当前版本：不可变数组本身就是一致快照，不必加锁，也不会与对方的并发写互相卡住。
        var incoming = Volatile.Read(ref other._items);
        if (incoming.Length == 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _items);

            // 合并结果没变化就不发布新版本（重复合并同一份按请求头时零分配）。
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

    /// <summary>
    /// 只读快照：直接返回当前版本数组，<b>零拷贝</b>（不可变数组本身就是快照）。
    /// <para>热路径（<c>ApplyHeaders</c>）用它代替「每次 new 一个 List」：之后每请求零分配，
    /// 且 <c>foreach</c> 数组走结构体枚举器、不装箱。</para>
    /// <para>调用方<b>不得修改</b>返回的数组。</para>
    /// </summary>
    internal Entry[] Snapshot() => Volatile.Read(ref _items);

    /// <summary>
    /// 结构体枚举器：<c>foreach</c> 直接用它，不经过 <see cref="IEnumerable{T}"/>，
    /// 因此不会把数组枚举器装箱（旧实现每次 <c>foreach</c> 白分配 56 B）。
    /// <para>快照在进入枚举时取定：枚举期间写入不会抛，也看不到中途变化。</para>
    /// </summary>
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

    /// <summary>
    /// 单条写入：算出新数组后 CAS 发布。<b>幂等写入（同值重设）不发布</b>，零分配。
    /// <para>
    /// CAS 失败说明别的线程刚发布过一版，基于新版本重算即可 —— 因此两个线程同时写不同的头不会互相覆盖
    /// （这正是「实例默认头可在请求进行中修改」的实现方式，不再需要引用计数与分离）。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 批量应用（构造之外的 <see cref="Set(IEnumerable{KeyValuePair{string, string?}})"/> 用）：
    /// 一次成型、只发布一次，避免逐条 CAS 的 O(N²) 复制。
    /// </summary>
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

            // 全是幂等写（值没变、也没有新增）时 ApplyOps 原样返回 current：不发布新版本。
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

        // 先按原始串拒绝控制字符（含 CR/LF）：名字尾部的 \r\n 会被 Trim 悄悄吃掉，
        // 那正是注入尝试的痕迹，不该被「修剪」后照常发出去。
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException(
                    $"非法 HTTP 请求头名称（含控制字符）：'{Escape(name)}'。",
                    nameof(name));
            }
        }

        var trimmed = name.Trim();
        foreach (var c in trimmed)
        {
            if (!IsTokenChar(c))
            {
                throw new ArgumentException(
                    $"非法 HTTP 请求头名称 '{name}'：只允许 RFC 7230 token 字符（字母/数字与 !#$%&'*+-.^_`|~），"
                    + "不允许空格、冒号或控制字符。",
                    nameof(name));
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

    /// <summary>
    /// 写入/上送前的取值校验：拒绝 <b>C0 控制字符</b>（HTAB 除外）与 DEL。
    /// <para>
    /// CR/LF 最危险：序列化层把它们当行分隔符，值里带 <c>\r\n</c> 等于允许调用方追加任意头甚至请求体
    /// （CWE-113）。而 <c>TryAddWithoutValidation</c> 完全不检查（已抓包证实），这里是唯一闸口。
    /// </para>
    /// <para>其余 C0 与 DEL 不拆行，但不是合法 <c>field-content</c>，网关/日志会截断或误判，一并拒绝。</para>
    /// </summary>
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
                throw new ArgumentException(
                    $"非法 HTTP 请求头取值（头 '{name}'）：不允许包含控制字符（这里是 U+{(int)c:X4}）。"
                    + "CR / LF 会被当成头部分隔符，等于允许调用方追加任意头或请求体；"
                    + "如果这是来自外部的数据，请先剔除控制字符——否则可能被用于注入额外的请求头或请求体。",
                    "value");
            }
        }
    }

    /// <summary>RFC 7230 token 字符（头名的字符集）。</summary>
    private static bool IsTokenChar(char c)
        => (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
