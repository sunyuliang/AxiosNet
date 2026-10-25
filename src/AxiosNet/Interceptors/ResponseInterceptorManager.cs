namespace AxiosNet.Interceptors;

internal sealed record ResponseInterceptor(
    int Id,
    Func<AxiosResponse, Task<AxiosResponse>> OnFulfilled,
    Func<Exception, Task<AxiosResponse>>? OnRejected);

/// <summary>
/// 响应拦截器管理器，对应 axios.interceptors.response，按注册顺序执行。
/// </summary>
public sealed class ResponseInterceptorManager
{
    private readonly List<ResponseInterceptor> _interceptors = new();
    private int _nextId;

    // 快照缓存：注册/弹出/清空才失效，避免每请求重建 List。
    // 用数组而不是 IReadOnlyList：数组的 foreach 编译成下标循环，不会把枚举器装箱。
    private ResponseInterceptor[]? _snapshot;

    /// <summary>注册异步拦截器，返回拦截器 id（可用于 Eject）。</summary>
    public int Use(
        Func<AxiosResponse, Task<AxiosResponse>> onFulfilled,
        Func<Exception, Task<AxiosResponse>>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(onFulfilled);
        lock (_interceptors)
        {
            var id = ++_nextId;
            _interceptors.Add(new ResponseInterceptor(id, onFulfilled, onRejected));
            Volatile.Write(ref _snapshot, null);
            return id;
        }
    }

    /// <summary>注册同步拦截器。</summary>
    public int Use(
        Func<AxiosResponse, AxiosResponse> onFulfilled,
        Func<Exception, AxiosResponse>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(onFulfilled);
        return Use(
            response => Task.FromResult(onFulfilled(response)),
            onRejected is null ? null : ex => Task.FromResult(onRejected(ex)));
    }

    /// <summary>移除指定拦截器（对应 axios eject）。</summary>
    public bool Eject(int id)
    {
        lock (_interceptors)
        {
            var removed = _interceptors.RemoveAll(i => i.Id == id) > 0;
            if (removed)
            {
                Volatile.Write(ref _snapshot, null);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_interceptors)
        {
            _interceptors.Clear();
            Volatile.Write(ref _snapshot, null);
        }
    }

    public int Count
    {
        get
        {
            lock (_interceptors)
            {
                return _interceptors.Count;
            }
        }
    }

    internal ResponseInterceptor[] Snapshot()
    {
        // 稳态零锁：快照是不可变数组，写侧只在注册 / 弹出 / 清空时换引用。
        // 只在快照失效后的第一次读取才进锁重建（每次请求都会走这里，锁是热路径开销）。
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_interceptors)
        {
            // 锁内复查：快照失效后可能有多条请求线程同时在这里排队，
            // 不复查的话每个都会重建一次（只有最后一个生效，前面几次纯属白分配）。
            snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null)
            {
                return snapshot;
            }

            snapshot = _interceptors.ToArray();
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }
}
