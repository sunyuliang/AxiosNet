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
/// 分离的原子性由 <see cref="EnterWrite"/> 保证：<b>判定独占、复制字典、发布新存储</b>与后续写入
/// 处在同一个临界区里，中途不会被 <see cref="Clone"/> 或另一个写方插队。
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
    /// <para>
    /// <b>锁就是 Store 自己</b>：早先给每个存储单独 new 一个锁对象，每个实例白付 24 B，
    /// 还要多一次间接寻址。类型是私有的，外部拿不到引用，用它当锁没有「别人也来 lock 同一对象」的风险。
    /// </para>
    /// <para>
    /// <b>字典惰性创建</b>：空 headers 很常见（懒创建的 <c>Config.Headers</c>、只读一次的默认值），
    /// 为它们预建一个 80 B 的空字典纯属浪费。首次写入才建，读路径只多一次 null 检查。
    /// </para>
    /// </summary>
    private sealed class Store
    {
        /// <summary>实际数据；<c>null</c> 表示空（尚未写入过）。只在锁内读写。</summary>
        internal Dictionary<string, string>? Map;

        /// <summary>
        /// 指向本存储的实例数（含自己）。只在锁内改。
        /// <para>
        /// 用 <see cref="long"/> 而不是 <see cref="int"/>：这个计数只在写入时递减（GC 不减），
        /// 长期高频的「克隆后从不写入」会把计数一路推高；int 在 21 亿次后溢出成负数，
        /// <see cref="EnterWrite"/> 的 <c>Owners == 1</c> 判定随之中毒（返回仍在共享的存储）。
        /// </para>
        /// </summary>
        internal long Owners = 1;

        /// <summary>
        /// 惰性生成的只读数组快照：枚举与上送读取都走它，写入时置空。
        /// 默认头在进程生命周期里基本不变，实际只会建一次。
        /// </summary>
        internal KeyValuePair<string, string>[]? Frozen;

        internal Store()
        {
        }

        /// <summary>分离用：整份复制（走字典的复制构造，比逐条重插快）。</summary>
        internal Store(Dictionary<string, string> source)
        {
            Map = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>取字典用于写入：没有就按需建一个。只在独占时调用（锁内，或对象尚未发布的构造期）。</summary>
        internal Dictionary<string, string> EnsureMap()
            => Map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取字典用于读取：没有返回 <c>null</c>（调用方按「空」处理，不分配）。</summary>
        internal Dictionary<string, string>? MapOrNull => Map;
    }

    /// <summary>
    /// 当前存储。只通过 <see cref="Volatile"/> 读写：写入方在 <see cref="EnterWrite"/> 里发布新存储，
    /// 读者在锁外取引用 —— 普通字段的读可能被 JIT 提到锁之前，读到已经被分离掉的旧存储。
    /// </summary>
    private Store _store;

    public AxiosHeaders() => _store = new Store();

    private AxiosHeaders(Store store) => _store = store;

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        : this()
    {
        ArgumentNullException.ThrowIfNull(headers);

        // 已知数量就一次备好容量：逐条 Set 会让字典按 3/5/9… 反复扩容（10 头实测多分配约 260 B）。
        // 构造期还没有第二个实例（Owners == 1），不必走 EnterWrite。
        if (headers is ICollection<KeyValuePair<string, string>> known && known.Count > 0)
        {
            _store.EnsureMap().EnsureCapacity(known.Count);
        }

        foreach (var kv in headers)
        {
            Set(kv.Key, kv.Value);
        }
    }

    /// <summary>设置/覆盖请求头，返回当前对象以支持链式调用。<c>null</c> 取值等价于删除。</summary>
    /// <exception cref="ArgumentException">名称不是合法 HTTP token，或取值含控制字符。</exception>
    public AxiosHeaders Set(string name, string? value)
    {
        var key = ValidateName(name);

        // null 表示删除：直接走 Delete，复用「不存在就不分离」的快速路径（不校验取值）。
        if (value is null)
        {
            Delete(key);
            return this;
        }

        ValidateHeaderValue(key, value);

        var store = EnterWrite();
        try
        {
            store.EnsureMap()[key] = value;
            InvalidateSnapshot(store);
        }
        finally
        {
            Monitor.Exit(store);
        }

        return this;
    }

    /// <summary>批量合并请求头（逐条校验、逐条生效，第 k 条非法时前 k-1 条已写入）。</summary>
    public AxiosHeaders Set(IEnumerable<KeyValuePair<string, string?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // 逐条 Set 是 n 次 Enter/Exit；批量时合并成一个临界区，语义保持不变。
        var store = EnterWrite();
        try
        {
            Dictionary<string, string>? map = null;

            if (headers is ICollection<KeyValuePair<string, string?>> { Count: > 0 } known)
            {
                map = store.EnsureMap();
                map.EnsureCapacity(map.Count + known.Count);
            }

            foreach (var kv in headers)
            {
                var key = ValidateName(kv.Key);

                // 删除：本来就没有字典的话，不必为了「删一个不存在的东西」把它建出来。
                if (kv.Value is null)
                {
                    (map ??= store.MapOrNull)?.Remove(key);
                    continue;
                }

                ValidateHeaderValue(key, kv.Value);
                (map ??= store.EnsureMap())[key] = kv.Value;
            }
        }
        finally
        {
            // 校验可能写到一半才抛：快照必须失效（部分写入已发生），锁也必须释放。
            InvalidateSnapshot(store);
            Monitor.Exit(store);
        }

        return this;
    }

    /// <summary>读取请求头（大小写不敏感，名称允许带首尾空白）。不存在返回 <c>null</c>。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var store = Volatile.Read(ref _store);
        lock (store)
        {
            var map = store.MapOrNull;
            return map is not null && map.TryGetValue(NormalizeName(name), out var value) ? value : null;
        }
    }

    /// <summary>指定名称是否存在（大小写不敏感）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var store = Volatile.Read(ref _store);
        lock (store)
        {
            return store.MapOrNull?.ContainsKey(NormalizeName(name)) == true;
        }
    }

    /// <summary>删除指定请求头，返回是否真的删掉了条目。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
    public bool Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var key = NormalizeName(name);

        // 快速路径：不存在就不必付分离复制。共享期间删一个不存在的头是常见形态（调用方统一清默认项）。
        var probe = Volatile.Read(ref _store);
        lock (probe)
        {
            if (probe.MapOrNull?.ContainsKey(key) != true)
            {
                return false;
            }
        }

        var store = EnterWrite();
        try
        {
            if (store.MapOrNull?.Remove(key) != true)
            {
                return false;
            }

            InvalidateSnapshot(store);
            return true;
        }
        finally
        {
            Monitor.Exit(store);
        }
    }

    public void Clear()
    {
        // 快速路径：空的存储没有内容可清，共享期间更不该为它整份复制。
        var probe = Volatile.Read(ref _store);
        lock (probe)
        {
            if (probe.MapOrNull is null or { Count: 0 })
            {
                return;
            }
        }

        var store = EnterWrite();
        try
        {
            store.MapOrNull?.Clear();
            InvalidateSnapshot(store);
        }
        finally
        {
            Monitor.Exit(store);
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
            var store = Volatile.Read(ref _store);
            lock (store)
            {
                return store.MapOrNull?.Count ?? 0;
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
    /// 锁内复核 <c>_store</c>：并发写入可能刚把本实例分离到新存储上，此时必须换新存储重来，
    /// 否则副本会拿到分离前的旧内容（而源已经在新存储上继续演进）。
    /// </para>
    /// </summary>
    public AxiosHeaders Clone()
    {
        while (true)
        {
            var store = Volatile.Read(ref _store);

            lock (store)
            {
                if (!ReferenceEquals(Volatile.Read(ref _store), store))
                {
                    continue; // 已被分离：重取当前存储，别给废弃的存储加计数
                }

                store.Owners++;
                return new AxiosHeaders(store);
            }
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

        var store = EnterWrite();
        try
        {
            var map = store.EnsureMap();

            // 一次把容量抬到「现有 + 新增」的上界：合并是每请求路径（默认头 + 按请求头），
            // 逐条写会让字典按 3/5/9… 反复扩容，白分配一轮桶与 entries 数组。
            map.EnsureCapacity(map.Count + incoming.Length);

            foreach (var kv in incoming)
            {
                map[kv.Key] = kv.Value;
            }

            InvalidateSnapshot(store);
        }
        finally
        {
            Monitor.Exit(store);
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
        var store = Volatile.Read(ref _store);
        var frozen = Volatile.Read(ref store.Frozen);
        if (frozen is not null)
        {
            return frozen;
        }

        lock (store)
        {
            var cached = Volatile.Read(ref store.Frozen);
            if (cached is not null)
            {
                return cached;
            }

            cached = Freeze(store);
            Volatile.Write(ref store.Frozen, cached);
            return cached;
        }
    }

    /// <summary>
    /// 结构体枚举器：<c>foreach</c> 直接用它，不经过 <see cref="IEnumerable{T}"/>，
    /// 因此不会把数组枚举器装箱（旧实现每次 <c>foreach</c> 白分配 56 B）。
    /// <para>快照在进入枚举时取定：枚举期间写入不会抛，也看不到中途变化。</para>
    /// </summary>
    public Enumerator GetEnumerator() => new(Snapshot());

    IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>只读结构体枚举器，基于 <see cref="Snapshot"/> 的数组。</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<string, string>>
    {
        private readonly KeyValuePair<string, string>[] _items;
        private int _index;

        internal Enumerator(KeyValuePair<string, string>[] items)
        {
            _items = items;
            _index = -1;
        }

        public KeyValuePair<string, string> Current => _items[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _items.Length;

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 进入「独占写」临界区：返回时<b>已持有</b>返回存储的锁，并保证
    /// <c>_store == 返回的存储</c> 且 <c>Owners == 1</c>。
    /// <para>
    /// 调用方必须在 <c>try/finally</c> 里配对 <see cref="Monitor.Exit(object)"/>。
    /// </para>
    /// <para>
    /// <b>为什么判定与写入必须同一个临界区</b>：旧实现是「先返回独占存储、调用方再另加一次锁」，
    /// 两步之间可以被插队 ——
    /// ① 另一个线程 <see cref="Clone"/> 把计数 +1，本次写入就漏给了副本；
    /// ② 另一个写方看到计数 &gt; 1 而分离，把 <c>_store</c> 换走，本次写入便落进一个再无人读取的孤儿存储。
    /// 两种都是丢更新。把判定、复制、发布与写入合并进一个临界区后，这两个窗口都不存在。
    /// </para>
    /// <para>
    /// 顺带省掉一次加锁：独占（绝大多数写入）时只 <c>Enter</c> 一次，旧实现要两次。
    /// </para>
    /// </summary>
    private Store EnterWrite()
    {
        while (true)
        {
            var store = Volatile.Read(ref _store);
            Monitor.Enter(store);

            // 进入锁之前可能已被别的线程分离：换它换上的新存储重来。
            if (!ReferenceEquals(Volatile.Read(ref _store), store))
            {
                Monitor.Exit(store);
                continue;
            }

            if (store.Owners == 1)
            {
                return store; // 锁保持，由调用方释放
            }

            // 仍在共享：复制一份并改挂到本实例，旧存储的计数减一。
            // 空存储直接给一个空的新存储，不为了「复制空字典」分配一个 80 B 的 Dictionary。
            var copy = store.MapOrNull is { } map ? new Store(map) : new Store();
            store.Owners--;
            Volatile.Write(ref _store, copy);
            Monitor.Exit(store);

            // 下一轮在 copy 上复核。copy 是全新的，通常直接返回；极端情况下刚发布就被 Clone，
            // 那就再复制一次 —— 无论如何都不会写进仍在共享的存储。
        }
    }

    /// <summary>让只读快照失效。必须在持有存储锁时调用。</summary>
    private static void InvalidateSnapshot(Store store) => Volatile.Write(ref store.Frozen, null);

    private static KeyValuePair<string, string>[] Freeze(Store store)
    {
        var map = store.MapOrNull;
        if (map is null or { Count: 0 })
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var array = new KeyValuePair<string, string>[map.Count];
        ((ICollection<KeyValuePair<string, string>>)map).CopyTo(array, 0);
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
