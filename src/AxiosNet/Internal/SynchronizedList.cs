namespace AxiosNet.Internal;

/// <summary>
/// 线程安全的 <see cref="IList{T}"/> 包装，用于 <see cref="AxiosConfig"/> 里的 transform 链。
/// <para>
/// transform 是运行期可追加/移除的配置，而请求线程会并发枚举；直接暴露 <see cref="List{T}"/>
/// 会在「枚举时写入」抛 <see cref="InvalidOperationException"/>。这里读写加锁，枚举返回快照。
/// </para>
/// </summary>
internal sealed class SynchronizedList<T> : IList<T>
{
    private readonly List<T> _items;
    private readonly object _sync = new();

    public SynchronizedList()
    {
        _items = new List<T>();
    }

    public SynchronizedList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 关键：不能直接把 items 交给 List<T>(IEnumerable)。
        // 那个构造函数发现源实现 ICollection<T> 时会走 Count + CopyTo 优化，
        // 两步之间若发生写入，数组尾部会残留 default(T)（委托即 null），后续调用直接 NRE。
        _items = items is SynchronizedList<T> synchronized
            ? synchronized.SnapshotList()
            : new List<T>(items);
    }

    /// <summary>在锁内复制一份底层列表，供拷贝构造使用。</summary>
    private List<T> SnapshotList()
    {
        lock (_sync)
        {
            return new List<T>(_items);
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_sync)
            {
                return _items[index];
            }
        }
        set
        {
            lock (_sync)
            {
                _items[index] = value;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(T item)
    {
        lock (_sync)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
        }
    }

    public bool Contains(T item)
    {
        lock (_sync)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_sync)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public int IndexOf(T item)
    {
        lock (_sync)
        {
            return _items.IndexOf(item);
        }
    }

    public void Insert(int index, T item)
    {
        lock (_sync)
        {
            _items.Insert(index, item);
        }
    }

    public bool Remove(T item)
    {
        lock (_sync)
        {
            return _items.Remove(item);
        }
    }

    public void RemoveAt(int index)
    {
        lock (_sync)
        {
            _items.RemoveAt(index);
        }
    }

    /// <summary>枚举快照：不会因为并发 Add/Remove 而抛异常。</summary>
    public IEnumerator<T> GetEnumerator()
    {
        List<T> snapshot;
        lock (_sync)
        {
            snapshot = new List<T>(_items);
        }

        return snapshot.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
