using System.Collections;

namespace AxiosNet.Internal;

/// <summary>
/// 线程安全的 <see cref="IList{T}"/> 包装，用于 <see cref="AxiosConfig"/> 里的 transform 链与按请求删头名单。
/// <para>
/// transform 是运行期可追加/移除的配置，而请求线程会并发枚举；直接暴露 <see cref="List{T}"/>
/// 会在「枚举时写入」抛 <see cref="InvalidOperationException"/>。这里写入发布新数组、读取取当前版本，
/// 枚举拿到的是一版不可变快照，不会抛、也不会看到中途变化。
/// </para>
/// <para>
/// <b>表示与 <see cref="AxiosHeaders"/> 同构</b>：实例只持有一个不可变数组，写入 = 算出新数组 + 原子换上。
/// 因此 <see cref="ShareIfNotEmpty"/>（每请求克隆时调用）<b>不取锁、不复制元素</b>，只让新实例指向同一版数组；
/// 任意一方写入都发布自己的新数组，语义与深拷贝一致。
/// </para>
/// <para>
/// 旧实现是「引用计数 + 分离复制」：每请求克隆要进一次 <c>lock</c>，写入要复制整份 <see cref="List{T}"/>，
/// 枚举还要为快照复制一份列表。改成不可变数组后这三项都不再随元素数增长。
/// </para>
/// </summary>
internal sealed class SynchronizedList<T> : IList<T>
{
    private static readonly T[] Empty = Array.Empty<T>();

    /// <summary>
    /// 越界消息。与 <see cref="List{T}"/> 的措辞对齐（英文原文是
    /// "Index was out of range. Must be non-negative and less than the size of the collection."），
    /// 这样「把 <c>List&lt;T&gt;</c> 换成写时复制的 IList」不会改变调用方看到的错误文案。
    /// </summary>
    private const string IndexOutOfRangeMessage = "索引超出范围：必须是非负数且小于集合大小。";

    /// <summary><see cref="Insert"/> 专用：允许插入到末尾，因此上界是「不大于」。</summary>
    private const string InsertIndexOutOfRangeMessage = "索引超出范围：必须是非负数且不大于集合大小。";

    /// <summary>
    /// 当前版本。只通过 <see cref="Volatile"/> 读、<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 写。
    /// </summary>
    private T[] _items;

    public SynchronizedList() => _items = Empty;

    public SynchronizedList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 源是本类型时直接取它当前的不可变版本（0 拷贝、也不会读到写了一半的状态）；
        // 其余情况复制成数组，与源彻底脱钩。
        _items = items is SynchronizedList<T> synchronized
            ? synchronized.Snapshot()
            : new List<T>(items).ToArray();
    }

    private SynchronizedList(T[] items) => _items = items;

    /// <summary>
    /// 写时复制的共享：不复制元素，只让新实例指向同一版数组；空列表返回 <c>null</c>
    /// （调用方只共享有内容的链，空链在克隆侧保持 <c>null</c>，热路径才不会为「空集合」付一次分配）。
    /// <para>「判空 + 共享」读的是同一个不可变版本，不存在旧实现里「判空之后被清空」的窗口。</para>
    /// </summary>
    internal SynchronizedList<T>? ShareIfNotEmpty()
    {
        var items = Volatile.Read(ref _items);
        return items.Length == 0 ? null : new SynchronizedList<T>(items);
    }

    /// <summary>
    /// 只读快照：直接返回当前版本数组，<b>零拷贝</b>。调用方<b>不得修改</b>返回的数组。
    /// <para>热路径（transform 链枚举）用它：旧实现要为此复制一份 <see cref="List{T}"/>。</para>
    /// </summary>
    internal T[] Snapshot() => Volatile.Read(ref _items);

    public T this[int index]
    {
        get
        {
            var items = Volatile.Read(ref _items);

            // 越界必须显式抛 ArgumentOutOfRangeException：本类型是公开的 IList<T>
            // （AxiosConfig.TransformRequest / RemoveHeaders），调用方按 BCL 契约 catch。
            // 直接索引数组会漏出 IndexOutOfRangeException，那是「实现细节泄漏成 API 行为」。
            if ((uint)index >= (uint)items.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);
            }

            return items[index];
        }
        set
        {
            while (true)
            {
                var current = Volatile.Read(ref _items);
                if ((uint)index >= (uint)current.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);
                }

                var next = new T[current.Length];
                Array.Copy(current, next, current.Length);
                next[index] = value;

                if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
                {
                    return;
                }
            }
        }
    }

    public int Count => Volatile.Read(ref _items).Length;

    public bool IsReadOnly => false;

    public void Add(T item)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);
            var next = new T[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = item;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
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

    public bool Contains(T item) => IndexOf(item) >= 0;

    public void CopyTo(T[] array, int arrayIndex) => Volatile.Read(ref _items).CopyTo(array, arrayIndex);

    public int IndexOf(T item)
    {
        var items = Volatile.Read(ref _items);
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < items.Length; i++)
        {
            if (comparer.Equals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    public void Insert(int index, T item)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);

            // Insert 允许 index == Count（追加），因此是「大于」而不是「大于等于」。
            if ((uint)index > (uint)current.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, InsertIndexOutOfRangeMessage);
            }

            var next = new T[current.Length + 1];
            Array.Copy(current, next, index);
            next[index] = item;
            Array.Copy(current, index, next, index + 1, current.Length - index);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
            }
        }
    }

    public bool Remove(T item)
    {
        var comparer = EqualityComparer<T>.Default;

        while (true)
        {
            var current = Volatile.Read(ref _items);

            // 快速路径：不存在就不发布新版本（共享期间移除一个没注册过的 transform 很常见）。
            var index = -1;
            for (var i = 0; i < current.Length; i++)
            {
                if (comparer.Equals(current[i], item))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return false;
            }

            var next = new T[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return true;
            }
        }
    }

    public void RemoveAt(int index)
    {
        while (true)
        {
            var current = Volatile.Read(ref _items);

            // 没有这道闸口时 `new T[current.Length - 1]` 在空表上会变成 `new T[-1]`，
            // 抛出的是 OverflowException（"Arithmetic operation resulted in an overflow"）——
            // 信息完全指不出「索引越界」，看起来像库内部算错了。
            if ((uint)index >= (uint)current.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);
            }

            var next = new T[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, current), current))
            {
                return;
            }
        }
    }

    /// <summary>
    /// 枚举快照：不会因为并发 Add/Remove 而抛异常。
    /// <para>
    /// 具体类型上还有 <see cref="Snapshot"/>（数组，<c>foreach</c> 零分配）；走接口时（如
    /// <c>ICollection&lt;string&gt;</c>）只能返回装箱的数组枚举器，库内热路径因此一律用 <see cref="Snapshot"/>。
    /// </para>
    /// </summary>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Volatile.Read(ref _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
