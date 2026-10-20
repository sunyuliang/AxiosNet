namespace AxiosNet.Interceptors;

internal sealed record RequestInterceptor(
    int Id,
    Func<AxiosConfig, Task<AxiosConfig>> OnFulfilled,
    Func<Exception, Task<AxiosConfig>>? OnRejected);

/// <summary>
/// 请求拦截器管理器，对应 axios.interceptors.request。
/// <para>
/// 执行顺序与 axios 一致：按注册顺序的**逆序**执行（后注册的先执行）。
/// </para>
/// <para>
/// <b>与 axios 的一处有意分歧</b>：<see cref="RequestInterceptor.OnRejected"/> 只捕获
/// <b>同一拦截器自己的 <c>OnFulfilled</c></b> 抛出的异常，不捕获发送阶段（建连 / 超时 / 取消 / 网络）的错误。
/// axios 里请求拦截器的 rejected 处在同一条链上，因此也能兜住 dispatch 异常，但它返回的 config 会被
/// 当作"再发一次请求"——一个只做日志的 rejected 就会把 POST 静默重发一遍。
/// 这里选择更窄也更可预期的语义：要感知发送错误，请用响应拦截器的 <c>OnRejected</c>（它能拿到所有异常）。
/// </para>
/// </summary>
public sealed class RequestInterceptorManager
{
    private readonly List<RequestInterceptor> _interceptors = new();
    private int _nextId;

    // 快照缓存：注册/弹出/清空才失效。请求热路径上避免每请求重建 List（含逆序副本）。
    private IReadOnlyList<RequestInterceptor>? _snapshot;

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
            _snapshot = null;
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

    /// <summary>快照并按 axios 规则逆序（后注册的先执行）；无变更时复用缓存。</summary>
    internal IReadOnlyList<RequestInterceptor> SnapshotReversed()
    {
        lock (_interceptors)
        {
            if (_snapshot is null)
            {
                var copy = new List<RequestInterceptor>(_interceptors);
                copy.Reverse();
                _snapshot = copy;
            }

            return _snapshot;
        }
    }
}
