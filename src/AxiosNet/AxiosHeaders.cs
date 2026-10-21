using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、同名覆盖（单值）、支持链式调用。
/// <para>
/// 所有读写都在存储锁内完成，因此实例默认头可在请求进行中安全增删（如运行期刷新 Token）。
/// 枚举返回快照，不持有实时视图。
/// </para>
/// <para>
/// <b>写时复制</b>：<see cref="Clone"/> 只加一次引用计数、共用同一份字典，任一方写入时才真正复制字典。
/// 分离是<b>对称</b>的：克隆方写入不影响源，源写入也不影响已克隆出去的副本。
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
    /// 共享存储。克隆出来的实例指向同一个 <see cref="Store"/>，<see cref="Owners"/> 记录实例数；
    /// 只要 &gt; 1，任何写入都必须先复制一份。
    /// <para>锁挂在存储上而不是实例上：共享期间多个实例必须串行访问同一份字典。</para>
    /// </summary>
    private sealed class Store
    {
        internal readonly object Sync = new();

        internal readonly Dictionary<string, string> Map;

        /// <summary>指向本存储的实例数（含自己）。只在持锁或 Interlocked 下改。</summary>
        internal int Owners = 1;

        /// <summary>
        /// 惰性生成的只读数组快照：枚举与上送读取都走它，写入时置空。
        /// 默认头在进程生命周期里基本不变，实际只会建一次。
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

        // 先只读探测：删除不存在（或共享存储上的探测）不必付一次分离复制。
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
    /// 之后任意一方写入都会先分离，语义与「深拷贝」一致，但只读用法不再为头数付费。
    /// </para>
    /// <para>
    /// 引用计数只在写入时递减，GC 时不减 —— 多出的计数只会让某次写入多复制一次字典，不影响正确性。
    /// </para>
    /// <para>
    /// 并发边界：克隆返回后的写入一律被分离挡住；唯一窗口是「写入方已判定独占、克隆紧接着完成」，
    /// 此时副本可能带上那次写入的值（方向只会是更新，且不会读到半成品 —— 读写都在同一把锁内）。
    /// </para>
    /// </summary>
    public AxiosHeaders Clone()
    {
        var store = _store;

        // 必须与 Exclusive() 的判定在同一把锁内：否则「写方判定 Owners 为 1」与「克隆方 +1」可以交错，
        // 副本会与一个仍在就地写入的存储重叠。
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

        // 先取快照：既是并发安全（对方可能同时在写），也避免两个实例互相合并时锁序相反造成死锁。
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
    /// 热路径（<c>ApplyHeaders</c>）用它代替「每次 new 一个 List」：数组首次读取时建好并缓存，
    /// 之后每请求零分配，且 <c>foreach</c> 数组走结构体枚举器、不装箱。
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
    /// 取一个「只属于自己」的存储：引用计数为 1 时直接返回，否则复制字典并降掉旧存储的计数。
    /// <para>判定必须在锁内：与 <see cref="Clone"/> 的 +1 互斥，保证「返回独占存储」在返回后依然成立。</para>
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
