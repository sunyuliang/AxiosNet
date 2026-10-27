using System.Collections;

namespace AxiosNet.Internal;

/// <summary>
/// 线程安全的 <see cref="IList{T}"/>，用于 <see cref="AxiosConfig"/> 的 transform 链与按请求删头名单。
/// 写入发布新的不可变数组，读取取当前版本，因此并发枚举不会抛异常。
/// </summary>
internal sealed class SynchronizedList<T> : IList<T>
{
    private static readonly T[] Empty = Array.Empty<T>();

    /// <summary>索引越界消息，措辞与 <see cref="List{T}"/> 保持一致。</summary>
    private const string IndexOutOfRangeMessage = "索引超出范围：必须是非负数且小于集合大小。";

    /// <summary><see cref="Insert"/> 专用：允许插入到末尾，因此上界是「不大于」。</summary>
    private const string InsertIndexOutOfRangeMessage = "索引超出范围：必须是非负数且不大于集合大小。";

    /// <summary>当前版本数组：<see cref="Volatile"/> 读，<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 写。</summary>
    private T[] _items;

    public SynchronizedList() => _items = Empty;

    public SynchronizedList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 源是同类型时共享它当前的数组，否则复制成独立数组。
        _items = items is SynchronizedList<T> synchronized ? synchronized.Snapshot() : new List<T>(items).ToArray();
    }

    private SynchronizedList(T[] items) => _items = items;

    /// <summary>把当前数组共享给新实例（不复制元素）；空列表返回 <c>null</c>。</summary>
    internal SynchronizedList<T>? ShareIfNotEmpty()
    {
        var items = Volatile.Read(ref _items);
        return items.Length == 0 ? null : new SynchronizedList<T>(items);
    }

    /// <summary>当前版本数组的快照（零拷贝），调用方不得修改。</summary>
    internal T[] Snapshot() => Volatile.Read(ref _items);

    public T this[int index]
    {
        get
        {
            var items = Volatile.Read(ref _items);
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

    /// <summary>清空：无条件发布空版本。</summary>
    /// <remarks>
    /// 这里刻意是「盲写常量」而不是 CAS 循环，别当成缺陷改：目标值是单例 <see cref="Empty"/>，整条路径没有「读-改-写」，
    /// 因此不存在可丢的更新 —— 与并发 <see cref="Add"/> / <see cref="Remove"/> 的任意交错都能线性化为「Add 在 Clear 之前」（元素被清掉，
    /// 合法）或「之后」（元素留下，合法）。改成 CAS 循环语义完全等价（CAS 失败后重读，照样写空，反而会清掉更多），
    /// 却会给人「Clear 只清它读到的那个版本」这种并不成立的印象。
    /// 前提是数组永不就地修改，<see cref="Empty"/> 与快照数组都只读；破坏这个前提才需要真正的提交换。
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

            // 不存在时不发布新版本。
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

    /// <summary>枚举当前快照，不会因为并发 Add/Remove 而抛异常。</summary>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Volatile.Read(ref _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
