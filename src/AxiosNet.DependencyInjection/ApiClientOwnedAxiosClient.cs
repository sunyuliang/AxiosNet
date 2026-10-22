namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// AddApiClient 自建模式与命名站点持有的底层 <see cref="AxiosClient"/>：容器释放单例（或释放解析器）时
/// 经它释放客户端（连接池不泄漏）。门面模式（复用容器中已注册的 <see cref="AxiosClient"/>）不涉及本类型。
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
