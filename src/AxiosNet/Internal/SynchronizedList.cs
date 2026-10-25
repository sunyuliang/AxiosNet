using System.Collections;

namespace AxiosNet.Internal;

/// <summary>
/// 线程安全的 <see cref="IList{T}"/> 包装，用于 <see cref="AxiosConfig"/> 里的 transform 链与按请求删头名单。
/// <para>
/// transform 是运行期可追加/移除的配置，而请求线程会并发枚举；直接暴露 <see cref="List{T}"/>
/// 会在「枚举时写入」抛 <see cref="InvalidOperationException"/>。这里读写加锁，枚举返回快照。
/// </para>
/// <para>
/// <b>写时复制</b>（与 <see cref="AxiosHeaders"/> 的 Owners 模型一致）：实例 defaults 上的列表会被每个请求的
/// 配置克隆共享（<see cref="ShareIfNotEmpty"/> 只递增引用计数，不复制元素）；任意一方首次写入前分离，
/// 语义与深拷一致，但只读用法（实例配了 transform、按请求不改）不再为元素数付费。
/// </para>
/// <para>
/// 分离的原子性由 <see cref="EnterWrite"/> 保证：<b>判定独占、复制列表、发布新状态</b>与后续写入
/// 处在同一个临界区里，中途不会被共享或另一个写方插队（旧实现两步分开，会丢更新）。
/// </para>
/// </summary>
internal sealed class SynchronizedList<T> : IList<T>
{
    /// <summary>
    /// 共享的底层状态。引用计数只在写入/分离时递减，GC 时不减 —— 多出的计数只会让某次写入多复制一次列表，不影响正确性。
    /// </summary>
    private sealed class Core
    {
        internal readonly List<T> Items;

        /// <summary>引用计数：共享本状态的实例数。</summary>
        internal long Owners;

        /// <summary>只读快照缓存：写入置空，之后每请求零分配（调用方不得修改返回的列表）。</summary>
        internal List<T>? Frozen;

        internal Core(List<T> items, long owners)
        {
            Items = items;
            Owners = owners;
        }
    }

    /// <summary>
    /// 当前共享状态。只通过 <see cref="Volatile"/> 读写：写方在 <see cref="EnterWrite"/> 里发布新状态，
    /// 读者在锁外取引用 —— 普通字段的读可能被 JIT 提到锁之前，读到已经被分离掉的旧状态。
    /// </summary>
    private Core _core;

    public SynchronizedList()
    {
        _core = new Core(new List<T>(), 1);
    }

    public SynchronizedList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 关键：不能直接把 items 交给 List<T>(IEnumerable)。
        // 那个构造函数发现源实现 ICollection<T> 时会走 Count + CopyTo 优化，
        // 两步之间若发生写入，数组尾部会残留 default(T)（委托即 null），后续调用直接 NRE。
        // 因此源是本类型时也在它的锁内整份复制（拷贝构造，与源不共享状态）。
        _core = new Core(items is SynchronizedList<T> synchronized ? synchronized.CopyUnderLock() : new List<T>(items), 1);
    }

    /// <summary>
    /// 写时复制的共享：不复制底层列表，只让引用计数 +1；空列表返回 <c>null</c>（调用方只共享有内容的链，
    /// 空链在克隆侧保持 <c>null</c>，热路径才不会为「空集合」付一次分配）。
    /// <para>
    /// 「是否为空」与「+1」必须在同一临界区：分成「先 Count 再 Share」的话，中间被清空就会共享出一个空链。
    /// </para>
    /// </summary>
    internal SynchronizedList<T>? ShareIfNotEmpty()
    {
        while (true)
        {
            var core = Volatile.Read(ref _core);

            lock (core)
            {
                if (!ReferenceEquals(Volatile.Read(ref _core), core))
                {
                    continue; // 已被分离：重取当前状态
                }

                if (core.Items.Count == 0)
                {
                    return null;
                }

                core.Owners++;
                return new SynchronizedList<T>(core);
            }
        }
    }

    private SynchronizedList(Core core)
    {
        _core = core;
    }

    /// <summary>拷贝构造的取用：在锁内整份复制，与源不共享任何状态。</summary>
    private List<T> CopyUnderLock()
    {
        var core = Volatile.Read(ref _core);
        lock (core)
        {
            return new List<T>(core.Items);
        }
    }

    /// <summary>
    /// 进入「独占写」临界区：返回时<b>已持有</b>返回状态的锁，并保证 <c>_core == 返回的状态</c> 且
    /// <c>Owners == 1</c>。调用方必须在 <c>try/finally</c> 里配对 <see cref="Monitor.Exit(object)"/>。
    /// <para>
    /// <b>为什么判定与写入必须同一个临界区</b>：旧实现「先返回独占状态、调用方再另加一次锁」，
    /// 两步之间另一个线程可以 <see cref="ShareIfNotEmpty"/>（+1，写入漏给副本）或再次分离
    /// （换走 <c>_core</c>，写入落进无人读取的孤儿状态）。对 transform 链来说，这两种都是
    /// 「注册过的 transform 悄悄不生效」，比 headers 更隐蔽。
    /// </para>
    /// </summary>
    private Core EnterWrite()
    {
        while (true)
        {
            var core = Volatile.Read(ref _core);
            Monitor.Enter(core);

            // 进入锁之前可能已被别的线程分离：换它换上的新状态重来。
            if (!ReferenceEquals(Volatile.Read(ref _core), core))
            {
                Monitor.Exit(core);
                continue;
            }

            if (core.Owners == 1)
            {
                return core; // 锁保持，由调用方释放
            }

            var copy = new Core(new List<T>(core.Items), 1);
            core.Owners--;
            Volatile.Write(ref _core, copy);
            Monitor.Exit(core);

            // 下一轮在 copy 上复核：copy 是全新的，通常直接返回。
        }
    }

    /// <summary>让只读快照失效。必须在持有状态锁时调用。</summary>
    private static void InvalidateSnapshot(Core core) => Volatile.Write(ref core.Frozen, null);

    public T this[int index]
    {
        get
        {
            var core = Volatile.Read(ref _core);
            lock (core)
            {
                return core.Items[index];
            }
        }
        set
        {
            var core = EnterWrite();
            try
            {
                core.Items[index] = value;
                InvalidateSnapshot(core);
            }
            finally
            {
                Monitor.Exit(core);
            }
        }
    }

    public int Count
    {
        get
        {
            var core = Volatile.Read(ref _core);
            lock (core)
            {
                return core.Items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(T item)
    {
        var core = EnterWrite();
        try
        {
            core.Items.Add(item);
            InvalidateSnapshot(core);
        }
        finally
        {
            Monitor.Exit(core);
        }
    }

    public void Clear()
    {
        // 快速路径：空列表没有内容可清，共享期间更不该为它整份复制。
        var probe = Volatile.Read(ref _core);
        lock (probe)
        {
            if (probe.Items.Count == 0)
            {
                return;
            }
        }

        var core = EnterWrite();
        try
        {
            core.Items.Clear();
            InvalidateSnapshot(core);
        }
        finally
        {
            Monitor.Exit(core);
        }
    }

    public bool Contains(T item)
    {
        var core = Volatile.Read(ref _core);
        lock (core)
        {
            return core.Items.Contains(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        var core = Volatile.Read(ref _core);
        lock (core)
        {
            core.Items.CopyTo(array, arrayIndex);
        }
    }

    public int IndexOf(T item)
    {
        var core = Volatile.Read(ref _core);
        lock (core)
        {
            return core.Items.IndexOf(item);
        }
    }

    public void Insert(int index, T item)
    {
        var core = EnterWrite();
        try
        {
            core.Items.Insert(index, item);
            InvalidateSnapshot(core);
        }
        finally
        {
            Monitor.Exit(core);
        }
    }

    public bool Remove(T item)
    {
        // 快速路径：不存在就不必付分离复制（共享期间移除一个没注册过的 transform 很常见）。
        var probe = Volatile.Read(ref _core);
        lock (probe)
        {
            if (probe.Items.IndexOf(item) < 0)
            {
                return false;
            }
        }

        var core = EnterWrite();
        try
        {
            if (!core.Items.Remove(item))
            {
                return false;
            }

            InvalidateSnapshot(core);
            return true;
        }
        finally
        {
            Monitor.Exit(core);
        }
    }

    public void RemoveAt(int index)
    {
        var core = EnterWrite();
        try
        {
            core.Items.RemoveAt(index);
            InvalidateSnapshot(core);
        }
        finally
        {
            Monitor.Exit(core);
        }
    }

    /// <summary>
    /// 只读快照：首次读取时建好并缓存，之后零分配，且 <c>foreach</c> 具体 <see cref="List{T}"/>
    /// 走结构体枚举器、不装箱。调用方<b>不得修改</b>返回的列表。
    /// </summary>
    internal List<T> Snapshot()
    {
        var core = Volatile.Read(ref _core);
        var frozen = Volatile.Read(ref core.Frozen);
        if (frozen is not null)
        {
            return frozen;
        }

        lock (core)
        {
            var cached = Volatile.Read(ref core.Frozen);
            if (cached is not null)
            {
                return cached;
            }

            cached = new List<T>(core.Items);
            Volatile.Write(ref core.Frozen, cached);
            return cached;
        }
    }

    /// <summary>枚举快照：不会因为并发 Add/Remove 而抛异常。</summary>
    public IEnumerator<T> GetEnumerator()
        => Snapshot().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
