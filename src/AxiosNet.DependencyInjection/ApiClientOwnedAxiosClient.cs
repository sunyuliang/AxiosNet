namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// AddApiClient 自建模式与命名站点持有的底层 <see cref="AxiosClient"/>：容器释放时经它释放客户端。
/// </summary>
internal sealed class ApiClientOwnedAxiosClient : IDisposable, IAsyncDisposable
{
    public ApiClientOwnedAxiosClient(AxiosConfig defaults)
    {
        Client = new AxiosClient(defaults);
    }

    public AxiosClient Client { get; }

    /// <summary>同步释放内部客户端，连接池随之关闭。</summary>
    public void Dispose() => Client.Dispose();

    /// <summary>异步释放内部客户端，避免阻塞连接关闭。</summary>
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
