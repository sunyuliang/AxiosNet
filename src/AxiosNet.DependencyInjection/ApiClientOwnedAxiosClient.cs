namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// AddApiClient 自建模式与命名站点持有的底层 <see cref="AxiosClient"/>：容器释放时经它释放客户端（连接池不泄漏）。
/// </summary>
internal sealed class ApiClientOwnedAxiosClient : IDisposable, IAsyncDisposable
{
    public ApiClientOwnedAxiosClient(AxiosConfig defaults)
    {
        Client = new AxiosClient(defaults);
    }

    public AxiosClient Client { get; }

    public void Dispose() => Client.Dispose();

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
