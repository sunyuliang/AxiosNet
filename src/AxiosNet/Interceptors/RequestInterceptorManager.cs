namespace AxiosNet.Interceptors;

internal sealed record RequestInterceptor(
    int Id,
    Func<AxiosConfig, Task<AxiosConfig>> OnFulfilled,
    Func<Exception, Task<AxiosConfig>>? OnRejected);

/// <summary>
/// 请求拦截器管理器，对应 axios.interceptors.request。
/// 注意：与 axios 一致，请求拦截器按注册顺序的逆序执行。
/// </summary>
public sealed class RequestInterceptorManager
{
    private readonly List<RequestInterceptor> _interceptors = new();
    private int _nextId;

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
            return id;
        }
    }

    /// <summary>注册同步拦截器。</summary>
    public int Use(
        Func<AxiosConfig, AxiosConfig> onFulfilled,
        Func<Exception, AxiosConfig>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(onFulfilled);
        return Use(
            config => Task.FromResult(onFulfilled(config)),
            onRejected is null ? null : ex => Task.FromResult(onRejected(ex)));
    }

    /// <summary>移除指定拦截器（对应 axios eject）。</summary>
    public bool Eject(int id)
    {
        lock (_interceptors)
        {
            return _interceptors.RemoveAll(i => i.Id == id) > 0;
        }
    }

    public void Clear()
    {
        lock (_interceptors)
        {
            _interceptors.Clear();
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

    /// <summary>快照并按 axios 规则逆序（后注册的先执行）。</summary>
    internal IReadOnlyList<RequestInterceptor> SnapshotReversed()
    {
        lock (_interceptors)
        {
            var copy = new List<RequestInterceptor>(_interceptors);
            copy.Reverse();
            return copy;
        }
    }
}
