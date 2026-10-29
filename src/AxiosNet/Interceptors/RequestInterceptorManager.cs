namespace AxiosNet.Interceptors;

internal sealed record RequestInterceptor(
    int Id,
    Func<AxiosConfig, Task<AxiosConfig>> OnFulfilled,
    Func<Exception, Task<AxiosConfig>>? OnRejected);

/// <summary>
/// 请求拦截器管理器，对应 axios.interceptors.request：按注册顺序的<b>逆序</b>执行（后注册的先执行）。
/// <see cref="RequestInterceptor.OnRejected"/> 只捕获同一拦截器自己的 <c>OnFulfilled</c> 异常，不捕获发送阶段的错误。
/// </summary>
public sealed class RequestInterceptorManager
{
    private readonly List<RequestInterceptor> _interceptors = new();
    private int _nextId;

    // 快照缓存：注册 / 弹出 / 清空时失效。
    private RequestInterceptor[]? _snapshot;

    /// <summary>注册异步拦截器，返回拦截器 id（可用于 Eject）。</summary>
    public int Use(
        Func<AxiosConfig, Task<AxiosConfig>> onFulfilled,
        Func<Exception, Task<AxiosConfig>>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(onFulfilled);
        lock (_interceptors)
        {
            var id = ++_nextId;
            _interceptors.Add(new RequestInterceptor(id, onFulfilled, onRejected));
            Volatile.Write(ref _snapshot, null);
            return id;
        }
    }

    /// <summary>注册同步拦截器。</summary>
    public int Use(
        Func<AxiosConfig, AxiosConfig> onFulfilled,
        Func<Exception, AxiosConfig>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(onFulfilled);
        return Use(config => Task.FromResult(onFulfilled(config)), onRejected is null ? null : ex => Task.FromResult(onRejected(ex)));
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

    /// <summary>快照并按 axios 规则逆序（后注册的先执行）；无变更时复用缓存。</summary>
    internal RequestInterceptor[] SnapshotReversed()
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
            Array.Reverse(snapshot);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }
}
