namespace AxiosNet.Pooling;

/// <summary>一次请求对连接池节点的租约，Dispose 后归还（只释放占用计数，TCP 连接仍留在池中复用）。</summary>
public sealed class PoolLease : IDisposable
{
    private readonly HttpConnectionPool _pool;
    private readonly PooledEndpoint _endpoint;
    private int _returned;

    internal PoolLease(HttpConnectionPool pool, PooledEndpoint endpoint, bool endpointReused)
    {
        _pool = pool;
        _endpoint = endpoint;
        EndpointReused = endpointReused;
    }

    /// <summary>该 origin 上复用的 HttpClient。</summary>
    public HttpClient Client => _endpoint.Client;

    /// <summary>租约对应的 origin（scheme://host:port）。</summary>
    public string Origin => _endpoint.Origin;

    /// <summary>
    /// 本次租约是否复用了池中已存在的 origin 节点；<c>false</c> 表示顺手新建了节点。
    /// 它不是「TCP 连接是否复用」，连接级复用发生在 <see cref="SocketsHttpHandler"/> 内部。
    /// </summary>
    public bool EndpointReused { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _pool.Return(_endpoint);
        }
    }
}
