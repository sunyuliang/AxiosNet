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
/// 配置克隆共享（<see cref="Share"/> 只递增引用计数，不复制元素）；任意一方首次写入前分离，
/// 语义与深拷一致，但只读用法（实例配了 transform、按请求不改）不再为元素数付费。
/// </para>
/// <para>
/// 并发边界：共享返回后的写入一律被分离挡住；唯一窗口是「写入方已判定独占、共享紧接着完成」，
/// 此时副本可能带上那次写入的值（方向只会是更新，且不会读到半成品 —— 读写都在同一把锁内）。
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
    /// 写时复制的共享：不复制底层列表，只让引用计数 +1。之后任意一方写入都会先分离，
    /// 语义与「深拷贝」一致，但只读用法不再为元素数付费。
    /// </summary>
    internal SynchronizedList<T> Share()
    {
        var core = _core;

        // 必须与 Exclusive 的判定在同一把锁内：否则「写方判定 Owners 为 1」与「共享方 +1」可以交错，
        // 副本会与一个仍在就地写入的列表重叠。
        lock (core)
        {
            core.Owners++;
            return new SynchronizedList<T>(core);
        }
    }

    private SynchronizedList(Core core)
    {
        _core = core;
    }

    /// <summary>拷贝构造的取用：在锁内整份复制，与源不共享任何状态。</summary>
    private List<T> CopyUnderLock()
    {
        var core = _core;
        lock (core)
        {
            return new List<T>(core.Items);
        }
    }

    /// <summary>
    /// 取一个「只属于自己」的底层状态：计数为 1 时直接返回，否则复制并降掉旧状态的计数。
    /// <para>判定必须在锁内：与 <see cref="Share"/> 的 +1 互斥，保证「返回的状态一定独占」在返回后依然成立。</para>
    /// <para>
    /// 拿到旧状态的引用后可能已被别的线程分离（<c>_core</c> 换成了新状态）：此时必须<b>重来一轮</b>，
    /// 不能直接把新状态交出去 —— 新状态可能刚被 <see cref="Share"/> 加过计数，
    /// 直接写入会让共享出来的副本看到这次写入（隔离性被破坏）。
    /// </para>
    /// </summary>
    private Core Exclusive()
    {
        while (true)
        {
            var core = _core;
            lock (core)
            {
                // 已被另一个实例（或本实例的另一个线程）分离过：换它换上的新状态重来，
                // 下一轮会在新状态的锁内复核 Owners —— 这是「返回的状态一定独占」的唯一保证。
                if (!ReferenceEquals(_core, core))
                {
                    continue;
                }

                if (core.Owners == 1)
                {
                    return core;
                }

                var copy = new Core(new List<T>(core.Items), 1);
                core.Owners--;
                Volatile.Write(ref _core, copy);
                return copy;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            var core = _core;
            lock (core)
            {
                return core.Items[index];
            }
        }
        set
        {
            var core = Exclusive();
            lock (core)
            {
                core.Items[index] = value;
                core.Frozen = null;
            }
        }
    }

    public int Count
    {
        get
        {
            var core = _core;
            lock (core)
            {
                return core.Items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(T item)
    {
        var core = Exclusive();
        lock (core)
        {
            core.Items.Add(item);
            core.Frozen = null;
        }
    }

    public void Clear()
    {
        var core = Exclusive();
        lock (core)
        {
            core.Items.Clear();
            core.Frozen = null;
        }
    }

    public bool Contains(T item)
    {
        var core = _core;
        lock (core)
        {
            return core.Items.Contains(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        var core = _core;
        lock (core)
        {
            core.Items.CopyTo(array, arrayIndex);
        }
    }

    public int IndexOf(T item)
    {
        var core = _core;
        lock (core)
        {
            return core.Items.IndexOf(item);
        }
    }

    public void Insert(int index, T item)
    {
        var core = Exclusive();
        lock (core)
        {
            core.Items.Insert(index, item);
            core.Frozen = null;
        }
    }

    public bool Remove(T item)
    {
        var core = Exclusive();
        lock (core)
        {
            if (!core.Items.Remove(item))
            {
                return false;
            }

            core.Frozen = null;
            return true;
        }
    }

    public void RemoveAt(int index)
    {
        var core = Exclusive();
        lock (core)
        {
            core.Items.RemoveAt(index);
            core.Frozen = null;
        }
    }

    /// <summary>
    /// 只读快照：首次读取时建好并缓存，之后零分配，且 <c>foreach</c> 具体 <see cref="List{T}"/>
    /// 走结构体枚举器、不装箱。调用方<b>不得修改</b>返回的列表。
    /// </summary>
    internal List<T> Snapshot()
    {
        var core = _core;
        var frozen = Volatile.Read(ref core.Frozen);
        if (frozen is not null)
        {
            return frozen;
        }

        lock (core)
        {
            return core.Frozen ??= new List<T>(core.Items);
        }
    }

    /// <summary>枚举快照：不会因为并发 Add/Remove 而抛异常。</summary>
    public IEnumerator<T> GetEnumerator()
        => Snapshot().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
