using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、同名覆盖（单值）、支持链式调用。
/// <para>
/// 所有读写都在内部锁内完成，因此实例默认值（<see cref="Axios.Defaults"/> 等）可以在
/// 请求进行中安全地增删请求头（例如运行期刷新 Token），不会让并发的配置合并看到半成品集合。
/// 枚举返回快照，不持有实时视图。
/// </para>
/// <para>
/// <b>写时复制</b>：<see cref="Clone"/> 只增加一次引用计数、共用同一份字典（一次 40 B 的对象分配），
/// 直到任何一方写入才真正复制字典。默认头是典型的「每请求拷一份、之后只读」场景
/// —— 以前每克隆一次都要按头数付出 List + 逐条 <see cref="Set(string, string?)"/> 重校验 + 字典重插的代价。
/// 分离是<b>对称</b>的：克隆方写入不影响源，源写入也不影响已克隆出去的副本（两边各自复制）。
/// </para>
/// <para>
/// <b>写入即校验</b>：头部名称必须是 RFC 7230 的 token（不含空格、冒号、控制字符），
/// 取值不允许出现 <c>CR</c> / <c>LF</c>。请求头最终经由
/// <see cref="HttpRequestMessage.Headers"/> 的 <c>TryAddWithoutValidation</c> 上送，
/// 那一层不做任何检查——若把外部输入直接拼进头部，<c>\r\n</c> 会被当成头部分隔符，
/// 等于给调用方开了一个 HTTP 请求拆分（CWE-113）的口子。这里提前拒绝。
/// </para>
/// <para>
/// 读/删入口（<see cref="Get"/> / <see cref="Has"/> / <see cref="Delete"/>）对
/// <c>null</c> 名称抛 <see cref="ArgumentNullException"/> 而不是让空引用冒出来：
/// 头部名常来自配置或循环变量，传错时报出参数名比一句 <c>NullReferenceException</c> 更容易定位。
/// </para>
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    /// <summary>
    /// 共享存储。克隆出来的实例指向同一个 <see cref="Store"/>，<see cref="Owners"/> 记录有几个实例
    /// 在用；只要 &gt; 1，任何写入都必须先复制一份再改。
    /// <para>
    /// 锁挂在存储上而不是实例上：共享期间多个实例必须串行访问同一份字典，用各自的锁等于没锁。
    /// </para>
    /// </summary>
    private sealed class Store
    {
        internal readonly object Sync = new();

        internal readonly Dictionary<string, string> Map;

        /// <summary>指向本存储的实例数（含自己）。只在持锁或 Interlocked 下改。</summary>
        internal int Owners = 1;

        /// <summary>
        /// 惰性生成的只读数组快照：枚举/上线读取都走它，写一次就置空。
        /// 默认头这份内容在进程生命周期里基本不变，所以实际只会建一次。
        /// </summary>
        internal KeyValuePair<string, string>[]? Frozen;

        internal Store()
        {
            Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>分离用：整份复制（走字典的复制构造，比逐条重插快）。</summary>
        internal Store(Dictionary<string, string> source)
        {
            Map = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }
    }

    private Store _store;

    public AxiosHeaders() => _store = new Store();

    private AxiosHeaders(Store store) => _store = store;

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        : this()
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kv in headers)
        {
            Set(kv.Key, kv.Value);
        }
    }

    /// <summary>设置/覆盖请求头，返回当前对象以支持链式调用。</summary>
    /// <exception cref="ArgumentException">名称不是合法 HTTP token，或取值含控制字符。</exception>
    public AxiosHeaders Set(string name, string? value)
    {
        var key = ValidateName(name);

        // null 表示删除，不校验取值
        if (value is not null)
        {
            ValidateHeaderValue(key, value);
        }

        var store = Exclusive();
        lock (store.Sync)
        {
            if (value is null)
            {
                store.Map.Remove(key);
            }
            else
            {
                store.Map[key] = value;
            }

            store.Frozen = null;
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

    /// <summary>读取请求头（大小写不敏感，名称允许带首尾空白）。不存在返回 <c>null</c>。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var store = _store;
        lock (store.Sync)
        {
            return store.Map.TryGetValue(NormalizeName(name), out var value) ? value : null;
        }
    }

    /// <summary>指定名称是否存在（大小写不敏感）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var store = _store;
        lock (store.Sync)
        {
            return store.Map.ContainsKey(NormalizeName(name));
        }
    }

    /// <summary>删除指定请求头，返回是否真的删掉了条目。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var key = NormalizeName(name);
        var store = _store;

        // 先只读探测：删除不存在（或落在共享存储上的探测）不必付一次分离复制。
        lock (store.Sync)
        {
            if (!store.Map.ContainsKey(key))
            {
                return false;
            }
        }

        store = Exclusive();
        lock (store.Sync)
        {
            if (!store.Map.Remove(key))
            {
                return false;
            }

            store.Frozen = null;
            return true;
        }
    }

    public void Clear()
    {
        var store = Exclusive();
        lock (store.Sync)
        {
            if (store.Map.Count == 0)
            {
                return;
            }

            store.Map.Clear();
            store.Frozen = null;
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
            var store = _store;
            lock (store.Sync)
            {
                return store.Map.Count;
            }
        }
    }

    /// <summary>
    /// 写时复制的克隆：不复制字典、不重新校验，只让共享存储的引用计数 +1。
    /// <para>
    /// 之后任意一方写入都会先分离，因此语义与「深拷贝」一致，但只读用法（默认头 → 每请求一份）
    /// 不再为头数付费。
    /// </para>
    /// <para>
    /// 引用计数只在写入时递减，实例被 GC 回收时不会减 —— 多出来的计数只会让某次写入多复制一次
    /// 字典，不影响正确性（因此不需要终结器）。
    /// </para>
    /// <para>
    /// 并发边界：克隆<b>返回之后</b>发生的写入一律被分离挡住（写入方判定自己独占时才会就地改）。
    /// 唯一的不确定窗口是「写入方已判定独占、克隆紧接着完成后写入才落盘」这一瞬间 ——
    /// 此时副本可能带上那次写入的值。方向只会是「更新」，不会读到半成品：所有读写都在同一把锁内，
    /// 每次写入是若干个字典操作的原子串行。
    /// </para>
    /// </summary>
    public AxiosHeaders Clone()
    {
        var store = _store;

        // 必须与 Exclusive() 的判定在同一把锁内：否则「写方判定 Owner 数为 1」与「克隆方 +1」
        // 可以交错，克隆出来的副本会与一个仍然就地写入的存储重叠（旧实现每次整份复制，没有这个窗口）。
        lock (store.Sync)
        {
            store.Owners++;
            return new AxiosHeaders(store);
        }
    }

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        // 先取快照：既是并发安全（对方可能同时在写），也避免两个实例互相合并时持锁顺序相反造成死锁。
        // 快照是缓存的只读数组，正常情况下零分配。
        var incoming = other.Snapshot();
        if (incoming.Length == 0)
        {
            return;
        }

        var store = Exclusive();
        lock (store.Sync)
        {
            foreach (var kv in incoming)
            {
                store.Map[kv.Key] = kv.Value;
            }

            store.Frozen = null;
        }
    }

    /// <summary>
    /// 只读快照（数组）。调用方<b>不得修改</b>返回的数组。
    /// <para>
    /// 热路径（<c>ApplyHeaders</c> 把默认头写到 <see cref="HttpRequestMessage"/>）用它代替
    /// 「每次 new 一个 List」：数组在首次读取时建好并缓存，之后每请求零分配，
    /// 且 <c>foreach</c> 数组走结构体枚举器，不会有装箱。
    /// </para>
    /// </summary>
    internal KeyValuePair<string, string>[] Snapshot()
    {
        var store = _store;
        var frozen = Volatile.Read(ref store.Frozen);
        if (frozen is not null)
        {
            return frozen;
        }

        lock (store.Sync)
        {
            return store.Frozen ??= Freeze(store);
        }
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, string>>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// 取一个「只属于自己」的存储：引用计数为 1 时直接返回，否则复制一份字典并降掉旧存储的计数。
    /// <para>
    /// 判定必须在锁内：与 <see cref="Clone"/> 的计数 +1 互斥，保证「返回独占存储」这件事在返回后
    /// 依然成立（否则克隆方可以在这之后把计数改成 2，而本实例已经开始就地写入）。
    /// </para>
    /// </summary>
    private Store Exclusive()
    {
        var store = _store;
        lock (store.Sync)
        {
            // 双重检查：另一个实例可能已经分离过，或自己刚刚被降为独占。
            if (!ReferenceEquals(_store, store) || store.Owners == 1)
            {
                return _store;
            }

            var copy = new Store(store.Map);
            store.Owners--;
            Volatile.Write(ref _store, copy);
            return copy;
        }
    }

    private static KeyValuePair<string, string>[] Freeze(Store store)
    {
        if (store.Map.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var array = new KeyValuePair<string, string>[store.Map.Count];
        ((ICollection<KeyValuePair<string, string>>)store.Map).CopyTo(array, 0);
        return array;
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

        // 先按原始串拒绝控制字符（含 CR/LF）：名字尾部带 \r\n 会被 Trim 悄悄吃掉，
        // 那正是注入尝试留下的痕迹，不该被「修剪」掉再照常发出去。
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
    /// 写入/上线前的取值校验：拒绝 <b>C0 控制字符</b>（HTAB 除外）与 DEL。
    /// <para>
    /// CR/LF 最危险：HTTP 序列化层把它们当行分隔符，值里带 <c>\r\n</c> 就等于允许调用方
    /// 追加任意头甚至请求体（CWE-113 请求拆分）。而请求头最终经
    /// <see cref="HttpRequestMessage.Headers"/> 的 <c>TryAddWithoutValidation</c> 上送，
    /// 那一层**完全不检查** CR/LF（已抓包证实），所以这里是唯一的闸口。
    /// </para>
    /// <para>
    /// 其余 C0（VT / FF / NUL / SOH…）与 DEL 不拆行，但都不是合法 <c>field-content</c>：
    /// 底层照样原样写上线，网关/日志组件会截断或误判，因此一并拒绝。HTAB 是合法空白，放行。
    /// </para>
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
