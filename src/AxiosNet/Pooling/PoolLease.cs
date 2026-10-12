namespace AxiosNet.Pooling;

/// <summary>
/// 一次请求对连接池节点的租约，using 结束后归还（仅释放占用计数，TCP 连接仍保留在池中复用）。
/// </summary>
public sealed class PoolLease : IDisposable
{
    private readonly HttpConnectionPool _pool;
    private readonly PooledEndpoint _endpoint;
    private int _returned;

    internal PoolLease(HttpConnectionPool pool, PooledEndpoint endpoint)
    {
        _pool = pool;
        _endpoint = endpoint;
    }

    /// <summary>该 origin 上复用的 HttpClient。</summary>
    public HttpClient Client => _endpoint.Client;

    /// <summary>租约对应的 origin（scheme://host:port）。</summary>
    public string Origin => _endpoint.Origin;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _pool.Return(_endpoint);
        }
    }
}
