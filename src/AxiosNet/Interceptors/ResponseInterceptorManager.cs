namespace AxiosNet.Interceptors;

/// <summary>一条响应拦截器：<c>Id</c> 用于 Eject，<c>OnRejected</c> 只兜自身 <c>OnFulfilled</c> 的异常。</summary>
/// <param name="Id">注册时分配的编号，Eject 按它定位；只增不复用。</param>
/// <param name="OnFulfilled">正常路径：拿到响应、改完再交回。</param>
/// <param name="OnRejected">补偿路径：只处理本拦截器 <c>OnFulfilled</c> 抛出的异常；null 表示不处理。</param>
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
    // 单调递增的拦截器编号（Eject 用），只增不复用。
    private int _nextId;

    // 快照缓存：注册 / 弹出 / 清空时失效。
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
        return Use(response => Task.FromResult(onFulfilled(response)), onRejected is null ? null : ex => Task.FromResult(onRejected(ex)));
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

    /// <summary>移除全部拦截器。</summary>
    public void Clear()
    {
        lock (_interceptors)
        {
            _interceptors.Clear();
            Volatile.Write(ref _snapshot, null);
        }
    }

    /// <summary>当前拦截器数量。</summary>
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

    /// <summary>取不可变快照：稳态无锁，重建在锁内复查一次。</summary>
    internal ResponseInterceptor[] Snapshot()
    {
        // 稳态零锁：快照是不可变数组。
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_interceptors)
        {
            // 锁内复查：多条线程同时排队时只重建一次。
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
