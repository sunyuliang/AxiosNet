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

    // 快照缓存：注册/弹出/清空才失效。请求热路径上避免每请求重建 List。
    private IReadOnlyList<ResponseInterceptor>? _snapshot;

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
            _snapshot = null;
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
                _snapshot = null;
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_interceptors)
        {
            _interceptors.Clear();
            _snapshot = null;
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

    internal IReadOnlyList<ResponseInterceptor> Snapshot()
    {
        lock (_interceptors)
        {
            if (_snapshot is null)
            {
                _snapshot = new List<ResponseInterceptor>(_interceptors);
            }

            return _snapshot;
        }
    }
}
